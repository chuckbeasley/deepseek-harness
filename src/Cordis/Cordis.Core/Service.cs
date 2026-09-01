namespace Harness.Cordis.Core;

/// <summary>
/// Base class for services that expose a named API on <c>ctx</c> (C# port of the vendored Cordis
/// Service).
///
/// A subclass calls <c>base(ctx, key)</c> from its constructor. The service registers itself in
/// the context under its key immediately — the registration is an effect, so the
/// context stops and removes it on dispose. Lifecycle hooks: <see cref="StartAsync"/> runs after
/// the service is registered (the Phase 1 loader calls it on activation), and <see cref="StopAsync"/>
/// runs once during teardown, before the entry is removed.
/// </summary>
public abstract class Service : IDisposable, IAsyncDisposable
{
    private bool _stopped;

    /// <summary>The context this service is registered in.</summary>
    public Context Ctx { get; }

    /// <summary>The service key this instance is registered under.</summary>
    public string Name { get; }

    /// <summary>
    /// Register this instance as <paramref name="key"/> in <paramref name="ctx"/> (the registration
    /// is an effect; disposal of the context stops and unregisters the service).
    /// </summary>
    protected Service(Context ctx, string key)
    {
        Ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Name = key ?? throw new ArgumentNullException(nameof(key));
        Ctx.Set(Name, this);
    }

    /// <summary>Lifecycle hook run when the service is activated (after registration).</summary>
    public virtual ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Lifecycle hook run once during teardown, before the service entry is removed.</summary>
    public virtual ValueTask StopAsync() => ValueTask.CompletedTask;

    internal ValueTask StopOnceAsync()
    {
        if (_stopped) return ValueTask.CompletedTask;
        _stopped = true;
        return StopAsync();
    }

    /// <summary>
    /// Stop the service and unregister it from the context. The fiber-level registration effect
    /// stays registered and is a no-op when it later runs (stop and removal are idempotent).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopOnceAsync();
        Ctx.RemoveService(Name, this);
    }

    /// <summary>Synchronous form of <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

