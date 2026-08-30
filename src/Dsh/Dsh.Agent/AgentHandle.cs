using Dsh.Session;

namespace Dsh.Agent;

/// <summary>
/// An owned agent plus its disposer, returned by <see cref="AgentRegistry.Register"/> (port of the
/// TS AgentHandle). The disposer is a capability: among consumers, only the holder can tear this
/// agent down. <see cref="Dispose"/> cancels the agent's lifecycle, detaches it from the registry
/// (emitting <c>agent/disposed</c>), and unwinds its scoped world.
/// </summary>
public sealed class AgentHandle : IDisposable, IAsyncDisposable
{
    private readonly AgentRegistry _registry;
    private IDisposable? _disposer;
    private bool _disposed;

    internal AgentHandle(AgentRegistry registry, Agent agent)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>The live agent owned by this handle.</summary>
    public Agent Agent { get; }

    /// <summary>
    /// Attach the registry effect disposer that detaches the agent. Called by the registry right
    /// after the registration effect commits.
    /// </summary>
    internal void Attach(IDisposable disposer) => _disposer = disposer;

    /// <summary>Abort the agent's active activity with an optional cause.</summary>
    public void Cancel(TurnEndCancelCause? cause = null) => Agent.Cancel(cause);

    /// <summary>
    /// Tear the agent down: cancel, detach from the registry, and unwind its scoped world.
    /// Idempotent; a registry-context disposal that already detached the agent leaves this a no-op.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Agent.Cancel();
        _disposer?.Dispose();
    }

    /// <summary>Asynchronous form of <see cref="Dispose"/> (detachment is synchronous in this port).</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
