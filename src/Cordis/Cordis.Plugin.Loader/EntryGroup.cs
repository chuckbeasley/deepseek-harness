using Harness.Cordis.Core;

namespace Harness.Cordis.Plugin.Loader;

/// <summary>Runtime owner for a list of child loader entries (port of the vendored EntryGroup).</summary>
public sealed class EntryGroup
{
    /// <summary>Context property key stamped on group rows (port of <c>Symbol.for('cordis.group')</c>).</summary>
    public const string Key = "cordis.group";

    private readonly Context _ctx;

    internal EntryGroup(Context ctx, EntryTree tree)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    /// <summary>The tree this group belongs to.</summary>
    public EntryTree Tree { get; }

    /// <summary>Row options of this group's children, in mount order.</summary>
    public List<EntryOptions> Data { get; } = new();

    /// <summary>The entry that mounts this group, or null for the tree's root group.</summary>
    public Entry? OwnerEntry { get; set; }

    internal Context Ctx => _ctx;

    /// <summary>
    /// Create (or refresh) one child entry and return its composite id. An existing entry is
    /// updated in place; a failed create rolls back the store and parent wiring.
    /// </summary>
    public async Task<string> CreateAsync(EntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var id = Tree.EnsureId(options);
        Tree.Store.TryGetValue(id, out var existing);
        var entry = existing ?? (Tree.Store[id] = new Entry(Tree.LoaderService));
        var previousParent = entry.Parent;
        entry.Parent = this;
        try
        {
            await entry.UpdateAsync(options, create: true, force: true);
        }
        catch
        {
            if (existing is not null)
            {
                entry.Parent = previousParent;
            }
            else
            {
                Tree.Store.Remove(id);
            }
            throw;
        }
        return entry.Id;
    }

    /// <summary>Remove a row from this group's data list.</summary>
    public void Unlink(EntryOptions options)
    {
        Data.Remove(options);
    }

    /// <summary>Stop and remove one child entry by its local id.</summary>
    public async Task RemoveAsync(string id, bool isDispose = false)
    {
        if (!Tree.Store.TryGetValue(id, out var entry)) return;
        await entry.DisposeFiberAsync(entry.Fiber);
        if (!isDispose) Unlink(entry.Options);
        Tree.Store.Remove(id);
        _ctx.Emit("loader/partial-dispose", entry, entry.Options, false);
    }

    /// <summary>
    /// Transactional reconciliation (port of the vendored <c>EntryGroup.update</c>): every
    /// candidate row is created first and every outcome awaited, rows absent from the new config
    /// are then removed, and a failed candidate rolls the whole update back to the previous rows.
    /// The port runs candidates sequentially so the shared context stays single-threaded, while
    /// collecting every failure the way the TS concurrent start does.
    /// </summary>
    public async Task UpdateAsync(IReadOnlyList<EntryOptions> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var oldConfig = Data.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var options in config)
        {
            var id = Tree.EnsureId(options);
            if (!seen.Add(id)) throw new InvalidOperationException($"duplicate loader entry id: {id}");
        }
        var oldMap = oldConfig.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var newMap = config.ToDictionary(item => item.Id, StringComparer.Ordinal);

        try
        {
            var failures = new List<Exception>();
            foreach (var options in config)
            {
                try
                {
                    await CreateAsync(options);
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            // Disposal owns termination: the tree may go away while candidates settle, and those
            // failures no longer describe a live update to roll back.
            if (Tree.Disposed) return;
            if (failures.Count == 1) throw failures[0];
            if (failures.Count > 1) throw new AggregateException("loader entries failed to apply", failures);
            foreach (var id in oldMap.Keys)
            {
                if (!newMap.ContainsKey(id)) await RemoveAsync(id, true);
            }
            Data.Clear();
            Data.AddRange(config);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var id in newMap.Keys.Reverse())
            {
                if (oldMap.ContainsKey(id)) continue;
                try
                {
                    await RemoveAsync(id, true);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            foreach (var options in oldConfig)
            {
                try
                {
                    await CreateAsync(options);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            Data.Clear();
            Data.AddRange(oldConfig);
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException("loader entry rollback failed", new[] { error }.Concat(rollbackErrors));
            }
            throw;
        }
    }

    /// <summary>Dispose every child entry, preserving the data list.</summary>
    public async Task StopAsync()
    {
        foreach (var options in Data.ToList())
        {
            await RemoveAsync(options.Id, true);
        }
    }
}

/// <summary>
/// Plugin that mounts a nested entry group (port of the vendored <c>Group</c>). Applying it
/// reconciles the child rows; a config update re-reconciles them; disposal stops the group.
/// The entry's fiber disposes the subgroup before the plugin disposer, so the group stop is
/// awaited asynchronously there.
/// </summary>
public sealed class GroupPlugin : ILoaderPlugin, IUpdatablePlugin
{
    private readonly EntryGroup _group;

    internal GroupPlugin(EntryGroup group)
    {
        _group = group ?? throw new ArgumentNullException(nameof(group));
    }

    /// <summary>The entry group this plugin owns.</summary>
    public EntryGroup Group => _group;

    /// <inheritdoc/>
    public async ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config)
    {
        await _group.UpdateAsync(ToRows(config));
        return null;
    }

    /// <inheritdoc/>
    public async ValueTask UpdateAsync(object? config)
    {
        await _group.UpdateAsync(ToRows(config));
    }

    private static IReadOnlyList<EntryOptions> ToRows(object? config)
    {
        if (config is null) return Array.Empty<EntryOptions>();
        if (config is not IReadOnlyList<EntryOptions> rows)
        {
            throw new InvalidOperationException(
                $"group config must be a list of entry options, got {config.GetType().Name}");
        }
        return rows;
    }
}
