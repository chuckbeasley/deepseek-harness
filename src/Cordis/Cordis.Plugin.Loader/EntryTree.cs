using System.Reflection;
using Cordis.Core;

namespace Cordis.Plugin.Loader;

/// <summary>
/// Mutable tree of loader entries (port of the vendored EntryTree). Persistence is supplied by
/// subclasses via <see cref="Write"/>; the loader's own tree is in-memory.
/// </summary>
public abstract class EntryTree
{
    /// <summary>Separator joining composite entry ids.</summary>
    public const string Sep = ":";

    private readonly Context _ctx;

    /// <summary>Create the tree on <paramref name="ctx"/> and its root group.</summary>
    protected EntryTree(Context ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Root = new EntryGroup(ctx, this);
    }

    /// <summary>The context the tree's entries share.</summary>
    public Context Ctx => _ctx;

    /// <summary>Controls per-entry load logging.</summary>
    public bool EnableLogs { get; set; }

    /// <summary>Root group holding the tree's top-level rows.</summary>
    public EntryGroup Root { get; }

    /// <summary>Entries by local id, including nested group children.</summary>
    public Dictionary<string, Entry> Store { get; } = new(StringComparer.Ordinal);

    /// <summary>True once the tree is being disposed; live updates skip rollback afterwards.</summary>
    internal bool Disposed { get; private set; }

    /// <summary>The loader service that owns this tree (the tree itself for <see cref="Loader"/>).</summary>
    internal abstract Loader LoaderService { get; }

    /// <summary>
    /// Iterate entries in this tree, including nested group children. Callers snapshot the result
    /// before mutating the tree (TS exposes the same live generator).
    /// </summary>
    public IEnumerable<Entry> Entries()
    {
        foreach (var entry in Store.Values)
        {
            yield return entry;
        }
    }

    /// <summary>Pending import and lifecycle tasks owned by this tree.</summary>
    public IReadOnlyList<Task> GetTasks()
    {
        var tasks = new List<Task>();
        foreach (var entry in Entries().ToList())
        {
            if (entry.InitTask is { } init) tasks.Add(init);
            else if (entry.Fiber?.InertiaTask is { } inertia) tasks.Add(inertia);
        }
        return tasks;
    }

    /// <summary>
    /// Wait until the tree has no active import or lifecycle tasks and every fiber settled.
    /// Rechecks service-gated fibers after tasks drain, so entries whose dependencies appeared
    /// load here; throws the first settled fiber failure, or an aggregate for several.
    /// </summary>
    public async Task AwaitAsync()
    {
        while (true)
        {
            var tasks = GetTasks();
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks.Select(SettleAsync));
                continue;
            }
            await RecheckPendingAsync();
            tasks = GetTasks();
            if (tasks.Count > 0) continue;
            var failures = new List<Exception>();
            foreach (var entry in Entries().ToList())
            {
                try
                {
                    await entry.AwaitFiberAsync();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            if (failures.Count == 1) throw failures[0];
            if (failures.Count > 1) throw new AggregateException("loader fibers failed", failures);
            if (!GetTasks().Any()) return;
        }
    }

    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // allSettled: task failures surface through the entry fibers on the next pass.
        }
    }

    /// <summary>Start every pending fiber whose declared services are now present.</summary>
    internal async Task RecheckPendingAsync()
    {
        bool progress;
        do
        {
            progress = false;
            foreach (var entry in Entries().ToList())
            {
                var fiber = entry.Fiber;
                if (fiber is null || fiber.State != FiberState.Pending || !fiber.DependenciesSatisfied) continue;
                progress = true;
                await fiber.StartAsync();
                break;
            }
        } while (progress);
    }

    /// <summary>Assign a random local id to <paramref name="options"/> when it has none, and return it.</summary>
    public string EnsureId(EntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.Id))
        {
            do
            {
                options.Id = RandomHex();
            } while (Store.ContainsKey(options.Id));
        }
        return options.Id;
    }

    private static string RandomHex()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < 8; i++)
        {
            chars[i] = "0123456789abcdef"[Random.Shared.Next(16)];
        }
        return new string(chars);
    }

    /// <summary>
    /// Resolve an entry by id. Nested ids separated by <see cref="Sep"/> walk entry subtrees (the
    /// Include plugin in a later phase); group children live in this tree's store under their
    /// local id, matching the vendored resolver.
    /// </summary>
    public Entry Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var parts = id.Split(Sep);
        var final = parts[^1];
        EntryTree? tree = this;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!tree.Store.TryGetValue(parts[i], out var mid) || mid.Subtree is null)
            {
                throw new InvalidOperationException($"cannot resolve entry {id}");
            }
            tree = mid.Subtree;
        }
        if (!tree.Store.TryGetValue(final, out var entry))
        {
            throw new InvalidOperationException($"cannot resolve entry {id}");
        }
        return entry;
    }

    /// <summary>Resolve the group for a parent id, or the root group for null.</summary>
    public EntryGroup ResolveGroup(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Root;
        var entry = Resolve(id);
        if (entry.Subgroup is null) throw new InvalidOperationException($"entry {id} is not a group");
        return entry.Subgroup;
    }

    /// <summary>Create an entry in the root group or a nested group and persist.</summary>
    public async Task<string> CreateAsync(EntryOptions options, string? parent = null, int position = -1)
    {
        ArgumentNullException.ThrowIfNull(options);
        var group = ResolveGroup(parent);
        var id = await group.CreateAsync(options);
        var entry = Resolve(id);
        if (position < 0) group.Data.Add(entry.Options);
        else group.Data.Insert(position, entry.Options);
        group.Tree.Write();
        return id;
    }

    /// <summary>Stop and remove an entry from its parent group and persist.</summary>
    public async Task RemoveAsync(string id)
    {
        var entry = Resolve(id);
        await entry.Parent.RemoveAsync(id);
        entry.Parent.Tree.Write();
    }

    /// <summary>
    /// Update an entry and optionally move it to another group (port of the vendored
    /// <c>EntryTree.update</c>). The move is rolled back when the entry fails to update.
    /// </summary>
    public async Task UpdateAsync(string id, EntryPatch patch, string? parent = null, int? position = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var entry = Resolve(id);
        var source = entry.Parent;
        var sourceIndex = source.Data.IndexOf(entry.Options);
        var target = source;
        if (parent is not null)
        {
            target = ResolveGroup(parent);
            source.Unlink(entry.Options);
            if (position is null) target.Data.Add(entry.Options);
            else target.Data.Insert(position.Value, entry.Options);
            entry.Parent = target;
        }
        try
        {
            var merged = entry.Options.Clone();
            patch.ApplyTo(merged);
            await entry.UpdateAsync(merged, create: false, force: true);
        }
        catch (Exception error)
        {
            if (parent is not null)
            {
                target.Unlink(entry.Options);
                source.Data.Insert(sourceIndex < 0 ? source.Data.Count : sourceIndex, entry.Options);
                entry.Parent = source;
                try
                {
                    await entry.UpdateAsync(entry.Options.Clone(), create: false, force: true);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException($"failed to roll back loader entry move {id}", new[] { error, rollbackError });
                }
            }
            throw;
        }
        source.Tree.Write();
        if (!ReferenceEquals(target, source)) target.Tree.Write();
    }

    /// <summary>
    /// Import a plugin by name: <c>cordis:</c> builtins resolve from the loader's builtin table,
    /// everything else from the plugin catalog. The resolved value is normalized to an
    /// <see cref="ILoaderPlugin"/>; a registered <see cref="Type"/> is instantiated via reflection.
    /// </summary>
    internal async ValueTask<ILoaderPlugin> ImportPluginAsync(string name)
    {
        object? resolved;
        if (name.StartsWith("cordis:", StringComparison.Ordinal))
        {
            if (!LoaderService.Builtins.TryGetValue(name["cordis:".Length..], out resolved) || resolved is null)
            {
                throw new InvalidOperationException($"unknown cordis builtin: {name}");
            }
        }
        else
        {
            resolved = LoaderService.Catalog.Resolve(name);
            if (resolved is null) throw new InvalidOperationException($"cannot resolve plugin '{name}'");
        }
        return NormalizePlugin(resolved, name);
    }

    private static ILoaderPlugin NormalizePlugin(object? resolved, string name)
    {
        if (resolved is ILoaderPlugin plugin) return plugin;
        if (resolved is Type { IsAbstract: false } type && typeof(ILoaderPlugin).IsAssignableFrom(type))
        {
            return (ILoaderPlugin)Activator.CreateInstance(type)!;
        }
        throw new InvalidOperationException($"plugin '{name}' does not implement {nameof(ILoaderPlugin)}");
    }

    /// <summary>Persist current tree state. In-memory trees may implement this as a no-op.</summary>
    public abstract void Write();

    internal void MarkDisposed()
    {
        Disposed = true;
    }
}
