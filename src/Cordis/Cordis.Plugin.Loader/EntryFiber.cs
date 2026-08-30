using Cordis.Core;

namespace Cordis.Plugin.Loader;

/// <summary>
/// Runtime instance of one loader entry application (C# port of the Cordis fiber as mounted by the
/// loader). The port keeps one shared context per loader, so the fiber owns the plugin instance,
/// its disposer, and the pending/loading/active/failed lifecycle that Cordis scopes per fiber.
/// </summary>
public sealed class EntryFiber
{
    private readonly ILoaderPlugin _plugin;
    private bool _bodyStarted;
    private Task? _inertia;

    internal EntryFiber(Entry entry, ILoaderPlugin plugin)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    /// <summary>The entry this fiber belongs to.</summary>
    public Entry Entry { get; }

    /// <summary>The plugin instance whose body this fiber runs.</summary>
    public ILoaderPlugin Plugin => _plugin;

    /// <summary>Current lifecycle state.</summary>
    public FiberState State { get; private set; } = FiberState.Pending;

    /// <summary>Failure captured by a failed body; null while the fiber is not failed.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Disposer returned by the plugin body; null until the body runs.</summary>
    public IDisposable? Disposer { get; private set; }

    /// <summary>In-flight start task, exposed so the tree can await settlement.</summary>
    internal Task? InertiaTask => _inertia;

    /// <summary>
    /// Whether every declared injection is registered on the shared context. An absent dependency
    /// keeps the fiber pending; <see cref="EntryTree.AwaitAsync"/> rechecks once services appear.
    /// </summary>
    internal bool DependenciesSatisfied =>
        Entry.Options.Inject is not { } inject || inject.All(name => Entry.Ctx.Get<object>(name) is not null);

    /// <summary>
    /// Start the plugin body once its declared services are present. A fiber with absent
    /// dependencies stays <see cref="FiberState.Pending"/> without running the body; the loader
    /// rechecks it when services appear. Body failures are captured on the fiber
    /// (<see cref="FiberState.Failed"/>) and surfaced by <see cref="AwaitAsync"/>.
    /// </summary>
    internal async Task StartAsync()
    {
        if (State is FiberState.Active or FiberState.Failed or FiberState.Unloading or FiberState.Disposed) return;
        if (!DependenciesSatisfied)
        {
            State = FiberState.Pending;
            return;
        }
        if (_bodyStarted) return;
        _bodyStarted = true;
        State = FiberState.Loading;
        var task = RunBodyAsync();
        _inertia = task;
        try
        {
            await task;
        }
        finally
        {
            _inertia = null;
        }
    }

    /// <summary>Reject when the fiber failed, so loader settlement surfaces the failure.</summary>
    internal async Task AwaitAsync()
    {
        if (State == FiberState.Failed) throw Error!;
        var task = _inertia;
        if (task is not null) await task;
    }

    /// <summary>
    /// Unload the fiber: dispose the owning group's children first, then the plugin disposer.
    /// Idempotent.
    /// </summary>
    internal async Task DisposeAsync()
    {
        if (State is FiberState.Unloading or FiberState.Disposed) return;
        State = FiberState.Unloading;
        try
        {
            if (Entry.Subgroup is { } group) await group.StopAsync();
            if (Disposer is { } disposer)
            {
                Disposer = null;
                disposer.Dispose();
            }
        }
        finally
        {
            State = FiberState.Disposed;
        }
    }

    /// <summary>Propagate a config update to an updatable plugin (group rows re-reconcile children).</summary>
    internal async Task UpdateAsync(object? config)
    {
        if (_plugin is IUpdatablePlugin updatable) await updatable.UpdateAsync(config);
    }

    private async Task RunBodyAsync()
    {
        try
        {
            Disposer = await _plugin.ApplyAsync(Entry.Ctx, Entry.Options.Config);
            State = FiberState.Active;
        }
        catch (Exception error)
        {
            Error = error;
            State = FiberState.Failed;
            Disposer?.Dispose();
            Disposer = null;
        }
    }
}
