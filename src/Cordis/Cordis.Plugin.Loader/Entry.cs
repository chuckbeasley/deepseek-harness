using Cordis.Core;

namespace Cordis.Plugin.Loader;

/// <summary>One configured plugin node inside an <see cref="EntryTree"/>.</summary>
public sealed class Entry
{
    /// <summary>Context property key the loader stamps on entry fibers (port of <c>Symbol.for('cordis.entry')</c>).</summary>
    public const string Key = "cordis.entry";

    private Task? _initTask;
    private bool _disposing;

    internal Entry(Loader loader)
    {
        Loader = loader ?? throw new ArgumentNullException(nameof(loader));
        Loader.Ctx.Emit("loader/entry-init", this);
    }

    /// <summary>The loader that owns the containing tree.</summary>
    public Loader Loader { get; }

    /// <summary>The shared context of the owning loader.</summary>
    public Context Ctx => Loader.Ctx;

    /// <summary>Alias of <see cref="Ctx"/> matching the TS <c>entry.context</c> getter.</summary>
    public Context Context => Ctx;

    /// <summary>The group this entry currently belongs to.</summary>
    public EntryGroup Parent { get; set; } = null!;

    /// <summary>Current serialized options; updated transactionally by <see cref="UpdateAsync"/>.</summary>
    public EntryOptions Options { get; internal set; } = new();

    /// <summary>Running fiber, or null while the entry is not mounted.</summary>
    public EntryFiber? Fiber { get; internal set; }

    /// <summary>Subgroup mounted when this entry is a group row.</summary>
    public EntryGroup? Subgroup { get; internal set; }

    /// <summary>Nested tree mounted by this entry (the Include plugin in a later phase); always null in this port.</summary>
    public EntryTree? Subtree { get; internal set; }

    /// <summary>True while the loader is disposing this entry's fiber on the entry's behalf.</summary>
    internal bool IsDisposing => _disposing;

    /// <summary>Task of an in-flight init, exposed for tree settlement.</summary>
    internal Task? InitTask => _initTask;

    /// <summary>
    /// Composite id: the owning group entry's id plus this row's id, joined by
    /// <see cref="EntryTree.Sep"/>.
    /// </summary>
    public string Id
    {
        get
        {
            var id = Options.Id;
            if (Parent.OwnerEntry is { } owner) id = owner.Id + EntryTree.Sep + id;
            return id;
        }
    }

    /// <summary>True when this entry or any owning parent entry is disabled (group rows are always enabled).</summary>
    public bool Disabled => IsDisabled(Options);

    /// <summary>
    /// Start the configured plugin if the entry is enabled and not already mounted. Pending
    /// (service-gated) fibers stay pending; failures wrap the entry diagnostic.
    /// </summary>
    public async Task InitAsync()
    {
        if (_initTask is not null)
        {
            await _initTask;
            return;
        }
        var task = InitCoreAsync();
        _initTask = task;
        try
        {
            await task;
        }
        finally
        {
            _initTask = null;
        }
        await AwaitFiberAsync();
    }

    internal async Task RefreshAsync()
    {
        if (Fiber is not null) return;
        if (Disabled) return;
        await InitAsync();
    }

    internal async Task DisposeFiberAsync(EntryFiber? fiber)
    {
        if (fiber is null) return;
        if (ReferenceEquals(Fiber, fiber)) Fiber = null;
        _disposing = true;
        try
        {
            await fiber.DisposeAsync();
        }
        finally
        {
            _disposing = false;
        }
    }

    internal async Task AwaitFiberAsync()
    {
        var fiber = Fiber;
        if (fiber is null) return;
        try
        {
            await fiber.AwaitAsync();
        }
        catch (Exception error)
        {
            throw UpdateError("apply", Options, error);
        }
    }

    /// <summary>
    /// Transactional update (port of the vendored <c>Entry.update</c>): merge the candidate
    /// options, restart or dispose the fiber only when something changed, import a replacement
    /// plugin before disposing the old row, and restore the previous options and plugin when the
    /// candidate fails to apply.
    /// </summary>
    /// <param name="options">the candidate options; used whole for <paramref name="create"/>, merged over the current options otherwise.</param>
    /// <param name="create">replace the current options instead of merging.</param>
    /// <param name="force">proceed even when nothing changed.</param>
    internal async Task UpdateAsync(EntryOptions options, bool create = false, bool force = false)
    {
        var previousOptions = Options;
        var legacy = previousOptions.Clone();
        var candidate = create ? options.Clone() : previousOptions.Clone();
        if (!create) MergeFields(candidate, options);

        var diff = ComputeDiff(candidate, legacy);
        if (diff.Count == 0 && !force) return;

        // First start: mount the plugin, rolling back the options on failure. For create the
        // candidate IS the caller's options object, so group data keeps option identity.
        var previous = Fiber;
        if (previous is null)
        {
            Fiber = null;
            Options = candidate;
            try
            {
                if (!IsDisabled(candidate)) await InitAsync();
            }
            catch
            {
                Options = previousOptions;
                throw;
            }
            if (!create) Options = candidate;
            return;
        }

        // Disabling: dispose the fiber; a dispose failure restores the options.
        if (IsDisabled(candidate))
        {
            Options = candidate;
            try
            {
                await DisposeFiberAsync(previous);
            }
            catch (Exception error)
            {
                Options = previousOptions;
                throw UpdateError("dispose", candidate, error);
            }
            if (!create) Options = candidate;
            Ctx.Emit("loader/partial-dispose", this, legacy, true);
            return;
        }

        // Config-only change: propagate to the running fiber, rolling the config back on failure.
        var replace = diff.Contains("name") || diff.Contains("inject") || diff.Contains("group");
        if (!replace)
        {
            Options = candidate;
            try
            {
                await PatchContextAsync(diff);
            }
            catch (Exception error)
            {
                Options = previousOptions;
                try
                {
                    await PatchContextAsync(diff);
                }
                catch (Exception rollbackError)
                {
                    throw UpdateError("rollback", legacy, new AggregateException(error, rollbackError));
                }
                Ctx.Emit("loader/partial-dispose", this, candidate, true);
                throw UpdateError("apply", candidate, error);
            }
            if (!create) Options = candidate;
            Ctx.Emit("loader/partial-dispose", this, legacy, true);
            return;
        }

        // Replace: import the candidate plugin BEFORE disposing the old row, dispose, then start
        // the candidate; a failed start restores the previous plugin.
        ILoaderPlugin plugin;
        try
        {
            plugin = diff.Contains("name")
                ? await Loader.ImportPluginAsync(candidate.Name)
                : previous.Plugin;
        }
        catch (Exception error)
        {
            throw UpdateError("import", candidate, error);
        }

        var previousPlugin = previous.Plugin;
        Options = candidate;
        try
        {
            await DisposeFiberAsync(previous);
        }
        catch (Exception error)
        {
            Options = previousOptions;
            throw UpdateError("dispose", candidate, error);
        }

        try
        {
            await StartAsync(plugin);
        }
        catch (Exception error)
        {
            Options = previousOptions;
            try
            {
                await StartAsync(previousPlugin);
            }
            catch (Exception rollbackError)
            {
                throw UpdateError("rollback", legacy, new AggregateException(error, rollbackError));
            }
            Ctx.Emit("loader/partial-dispose", this, candidate, true);
            throw UpdateError("apply", candidate, error);
        }
        if (!create) Options = candidate;
        Ctx.Emit("loader/partial-dispose", this, legacy, true);
    }

    private async Task InitCoreAsync()
    {
        ILoaderPlugin plugin;
        try
        {
            // Group rows mount GroupPlugin without importing: their config IS the child rows.
            plugin = Options.Group == true
                ? new GroupPlugin(Subgroup ??= new EntryGroup(Ctx, Loader) { OwnerEntry = this })
                : await Loader.ImportPluginAsync(Options.Name);
        }
        catch (Exception error)
        {
            throw UpdateError("import", Options, error);
        }
        try
        {
            await StartAsync(plugin);
        }
        catch (Exception error)
        {
            throw UpdateError("apply", Options, error);
        }
    }

    private async Task StartAsync(ILoaderPlugin plugin)
    {
        EntryFiber? fiber = null;
        try
        {
            this.Loader.ShowLog(this, "apply");
            fiber = new EntryFiber(this, plugin);
            Fiber = fiber;
            await fiber.StartAsync();
            await fiber.AwaitAsync();
        }
        catch
        {
            if (fiber is not null) await DisposeFiberAsync(fiber);
            throw;
        }
    }

    private async Task PatchContextAsync(IReadOnlyList<string> diff)
    {
        // Port of the loader's patch-context hook: only config changes (or a group row) reach a
        // started fiber. Cordis dispatches this through an async 'loader/patch-context' waterfall
        // used by isolate; Cordis.Core's waterfall is synchronous, so the port runs the update
        // directly and omits the hook.
        if (Fiber is { } fiber && (diff.Contains("config") || Options.Group == true))
        {
            await fiber.UpdateAsync(Options.Config);
        }
    }

    private bool IsDisabled(EntryOptions options)
    {
        if (options.Group == true) return false;
        if (options.Disabled == true) return true;
        var owner = Parent.OwnerEntry;
        while (owner is not null)
        {
            if (owner.Options.Disabled == true) return true;
            owner = owner.Parent.OwnerEntry;
        }
        return false;
    }

    private static void MergeFields(EntryOptions target, EntryOptions source)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.Config = source.Config;
        target.Group = source.Group;
        target.Disabled = source.Disabled;
        target.Inject = source.Inject;
    }

    private static List<string> ComputeDiff(EntryOptions candidate, EntryOptions legacy)
    {
        var diff = new List<string>();
        if (!string.Equals(candidate.Id, legacy.Id, StringComparison.Ordinal)) diff.Add("id");
        if (!string.Equals(candidate.Name, legacy.Name, StringComparison.Ordinal)) diff.Add("name");
        if (!Deep.ValueEquals(candidate.Config, legacy.Config)) diff.Add("config");
        if (candidate.Group != legacy.Group) diff.Add("group");
        if (candidate.Disabled != legacy.Disabled) diff.Add("disabled");
        if (!SequenceEqual(candidate.Inject, legacy.Inject)) diff.Add("inject");
        return diff;
    }

    private static bool SequenceEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    internal static Exception UpdateError(string stage, EntryOptions options, Exception cause)
    {
        return new InvalidOperationException(
            $"failed to {stage} loader entry {options.Id} ({options.Name}): {cause.Message}", cause);
    }
}
