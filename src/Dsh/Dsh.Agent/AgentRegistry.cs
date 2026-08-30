using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Agent;

/// <summary>
/// Live agent registry (ctx service "agents", port of the TS AgentRegistry surface relevant to the
/// core spine). <see cref="Register"/> records an already-constructed agent as an effect: the
/// returned <see cref="AgentHandle"/> is the disposer, and disposing the registry's context detaches
/// every live entry too. Detachment removes the entry, emits <c>agent/disposed</c>, and unwinds the
/// agent's scoped world. Creation/resume through a factory (AgentLoop) is a later-phase deliverable.
/// </summary>
public sealed class AgentRegistry : Service
{
    private readonly Dictionary<SessionId, Agent> _store = new();

    /// <summary>Create the registry under <c>ctx</c>; it registers itself as the "agents" service.</summary>
    public AgentRegistry(Context ctx)
        : base(ctx, "agents")
    {
    }

    /// <summary>
    /// Register a live agent. The registration is an effect on the registry's context; disposing
    /// the returned handle (or the context) detaches the agent. Emits <c>agent/created</c> on
    /// registration and <c>agent/disposed</c> on detachment, each with listener failures contained.
    /// </summary>
    /// <param name="agent">the already-constructed agent to record.</param>
    /// <returns>the owned handle; only the holder can tear this agent down.</returns>
    /// <exception cref="InvalidOperationException">when the agent was constructed on a different
    /// context, or an agent with the same id is already registered.</exception>
    public AgentHandle Register(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!ReferenceEquals(agent.Owner, Ctx))
        {
            throw new InvalidOperationException(
                $"agent \"{agent.Id}\" was created on a different context; register it through the context it owns");
        }
        var handle = new AgentHandle(this, agent);
        var disposer = Ctx.Effect(() =>
        {
            Enter(agent);
            Announce(agent);
            return new ActionDisposer(() => Detach(agent));
        }, $"agents.register(\"{agent.Id}\")");
        handle.Attach(disposer);
        return handle;
    }

    /// <summary>Look up a live agent by its shared agent/session id.</summary>
    public Agent? Get(SessionId id) => _store.TryGetValue(id, out var agent) ? agent : null;

    /// <summary>All live agents, in registration order.</summary>
    public IReadOnlyList<Agent> List() => _store.Values.ToArray();

    /// <summary>Whether a live agent with <paramref name="id"/> is registered.</summary>
    public bool Contains(SessionId id) => _store.ContainsKey(id);

    /// <summary>Remove a live agent when the registry still holds it (idempotent).</summary>
    internal void Detach(Agent agent)
    {
        if (!_store.TryGetValue(agent.Id, out var current) || !ReferenceEquals(current, agent)) return;
        _store.Remove(agent.Id);
        EmitContained(AgentEvents.Disposed, new AgentDisposedPayload(agent));
        // The scoped world unwinds last, mirroring the TS handle teardown order (loop quiescence,
        // unregister, session removal, scoped unwind). No loop exists in this port, so the
        // registry's detach is the sole owner of the agent scope.
        agent.DisposeScope();
    }

    private void Enter(Agent agent)
    {
        if (_store.ContainsKey(agent.Id))
        {
            throw new InvalidOperationException($"agent \"{agent.Id}\" is already registered");
        }
        _store[agent.Id] = agent;
    }

    private void Announce(Agent agent)
    {
        EmitContained(AgentEvents.Created, new AgentCreatedPayload(agent));
    }

    private void EmitContained(string name, IAgentEventPayload payload)
    {
        try
        {
            Ctx.Emit(name, payload);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"agent \"{payload.Agent.Id}\": {name} listener threw: {error.Message}");
        }
    }
}
