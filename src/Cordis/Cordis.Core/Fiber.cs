namespace Cordis.Core;

/// <summary>
/// Lifecycle state of a plugin fiber (port of the vendored Cordis FiberState). Pending — waiting
/// for required services; Loading — the plugin body is running; Active — loaded and providing;
/// Failed — the body or its config threw; Unloading — disposers are running; Disposed — the fiber
/// was removed and cannot restart. Phase 0 exposes the root fiber only; the pending/loading
/// transitions become reachable when the plugin loader (Phase 1) mounts child fibers.
/// </summary>
public enum FiberState
{
    /// <summary>Waiting for required services to become available.</summary>
    Pending,

    /// <summary>The plugin body is running.</summary>
    Loading,

    /// <summary>Loaded and providing.</summary>
    Active,

    /// <summary>The body or its config threw; the fiber holds the error.</summary>
    Failed,

    /// <summary>Disposers are running.</summary>
    Unloading,

    /// <summary>The fiber was removed and cannot restart.</summary>
    Disposed,
}

/// <summary>Tree node describing one live effect for diagnostics (port of the TS EffectMeta).</summary>
public sealed record EffectMeta(string Label, IReadOnlyList<EffectMeta> Children);

/// <summary>
/// Effect cleanup holder registered on a fiber (internal; the fiber unload drives it).
///
/// Disposal is single-shot: the first call runs the collected cleanups in reverse collection
/// order and stores the in-flight task; repeated calls join that task instead of re-running it
/// (vendor/README item 6: Cordis's internal effect composition joins an already-running cleanup
/// while repeated public disposer calls retain their single-shot result).
/// </summary>
internal sealed class EffectNode : IDisposable, IAsyncDisposable
{
    /// <summary>Effect label shown in fiber diagnostics.</summary>
    public string Label { get; }

    /// <summary>Effects registered while this effect's setup ran (nested effects).</summary>
    public List<EffectNode> Children { get; } = new();

    private readonly List<Func<ValueTask>> _cleanups = new();
    private bool _disposed;
    private ValueTask? _disposal;

    public EffectNode(string label)
    {
        Label = label;
    }

    public void AddCleanup(Func<ValueTask> cleanup)
    {
        _cleanups.Add(cleanup);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return _disposal ?? ValueTask.CompletedTask;
        _disposed = true;
        _disposal = DisposeCore();
        return _disposal.Value;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async ValueTask DisposeCore()
    {
        for (int i = _cleanups.Count - 1; i >= 0; i--)
        {
            await _cleanups[i]();
        }
        _cleanups.Clear();
    }
}

/// <summary>
/// Runtime instance of one plugin application (C# port of the vendored Cordis Fiber).
///
/// A fiber owns the effects registered through its context. <see cref="Effect"/> runs a setup
/// immediately and collects the disposer it returns; disposal unwinds effects in reverse
/// registration order and contains per-effect cleanup failures, so one failing cleanup cannot
/// starve its peers (vendor/README item 6). Phase 0 constructs the root fiber of a context only;
/// the plugin loader (Phase 1) will mount one fiber per plugin.
/// </summary>
public sealed class Fiber
{
    private readonly Context _ctx;
    private readonly DisposableList<EffectNode> _effects = new();
    private EffectNode? _current;
    private readonly int _uid;

    /// <summary>Create the root fiber of a context; it starts in the <see cref="FiberState.Active"/> state.</summary>
    public Fiber(Context ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _uid = 0;
        State = FiberState.Active;
    }

    /// <summary>Unique id of this fiber within its registry; 0 for the root fiber.</summary>
    public int Uid => _uid;

    /// <summary>Current lifecycle state.</summary>
    public FiberState State { get; private set; }

    /// <summary>
    /// Throw when the fiber has been disposed (mirrors Cordis INACTIVE_EFFECT).
    /// </summary>
    /// <exception cref="CordisError">with code <see cref="CordisErrorCode.INACTIVE_EFFECT"/>.</exception>
    public void AssertActive()
    {
        if (State == FiberState.Disposed) throw new CordisError(CordisErrorCode.INACTIVE_EFFECT);
    }

    /// <summary>
    /// Register an effect on this fiber: <paramref name="execute"/> runs immediately and returns
    /// the disposer that releases its resources. The returned disposer is single-shot; the effect
    /// is also disposed when the fiber unloads, in reverse registration order, with per-effect
    /// error containment. A synchronous setup failure rolls back effects registered during the
    /// failed setup and rethrows.
    /// </summary>
    /// <param name="execute">the effect body; returns the cleanup disposer, or <c>null</c>.</param>
    /// <param name="label">effect label shown in <see cref="GetEffects"/> diagnostics.</param>
    /// <returns>a single-shot disposer running the cleanup.</returns>
    /// <exception cref="CordisError">with code <see cref="CordisErrorCode.INACTIVE_EFFECT"/> when the fiber is disposed or unloading.</exception>
    public IDisposable Effect(Func<IDisposable?> execute, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return (EffectNode)RegisterEffect(() =>
        {
            var cleanup = execute();
            return cleanup is null
                ? null
                : () =>
                {
                    cleanup.Dispose();
                    return ValueTask.CompletedTask;
                };
        }, label ?? "anonymous");
    }

    /// <summary>
    /// Async-teardown variant of <see cref="Effect"/>; the returned disposer is awaitable.
    /// </summary>
    public IAsyncDisposable EffectAsync(Func<IAsyncDisposable?> execute, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return RegisterEffect(() =>
        {
            var cleanup = execute();
            return cleanup is null ? null : () => cleanup.DisposeAsync();
        }, label ?? "anonymous");
    }

    /// <summary>
    /// Internal effect registration used by the context and core services. The owner-list wrapper
    /// is registered before setup runs, so an unload begun from inside setup awaits this effect
    /// and every cleanup it collected (vendor/README item 6).
    /// </summary>
    internal IAsyncDisposable RegisterEffect(Func<Func<ValueTask>?> execute, string label)
    {
        AssertActive();
        if (State == FiberState.Unloading) throw new CordisError(CordisErrorCode.INACTIVE_EFFECT);

        var node = new EffectNode(label);
        _effects.Push(node);
        var parent = _current;
        parent?.Children.Add(node);
        _current = node;
        try
        {
            var cleanup = execute();
            if (cleanup is not null) node.AddCleanup(cleanup);
        }
        catch
        {
            Rollback(node);
            throw;
        }
        finally
        {
            _current = parent;
        }
        return node;
    }

    /// <summary>
    /// Unload the fiber: run every effect's disposal in reverse registration order, containing
    /// failures (each is logged via the context logger and the remaining effects still run).
    /// Idempotent.
    /// </summary>
    internal async ValueTask UnloadAsync()
    {
        if (State == FiberState.Disposed) return;
        State = FiberState.Unloading;
        foreach (var node in _effects.Clear())
        {
            try
            {
                await node.DisposeAsync();
            }
            catch (Exception error)
            {
                _ctx.Logger.Error(error);
            }
        }
        State = FiberState.Disposed;
    }

    /// <summary>Return metadata for currently registered effects, one tree per live effect.</summary>
    public IReadOnlyList<EffectMeta> GetEffects()
    {
        return _effects.Select(ToMeta).ToList();
    }

    private static EffectMeta ToMeta(EffectNode node)
    {
        return new EffectMeta(node.Label, node.Children.Select(ToMeta).ToList());
    }

    private void Rollback(EffectNode node)
    {
        // A synchronous setup failure removes the wrapper and rolls back effects registered
        // during the failed setup (vendor/README item 6). Errors are contained like unload errors.
        var index = _effects.IndexOf(node);
        if (index < 0) return;
        for (int i = _effects.Count - 1; i > index; i--)
        {
            var child = _effects[i];
            _effects.RemoveAt(i);
            try
            {
                child.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                _ctx.Logger.Error(error);
            }
        }
        _effects.RemoveAt(index);
    }
}
