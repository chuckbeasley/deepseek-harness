namespace Dsh.AgentLoop;

/// <summary>
/// Concrete agent factory and driver service (ctx.agentLoop; port of the TS AgentLoop service
/// minus the declarative boot array, which belongs to the Phase 3 boot composition).
/// <see cref="Create"/> publishes a fresh agent and session under one caller-supplied identity;
/// <see cref="Resume"/> rehydrates a persisted session log and republishes the agent on it. Both
/// register the agent with the <c>agents</c> registry and announce <c>agent/session-start</c>.
/// Every published agent is driven by a <see cref="LoopAgent"/>; the registry's teardown
/// (handle disposal or context disposal) aborts the running turn through the linked lifecycle
/// signal. The service also installs the package's request-reconstruction invariant and owns the
/// runtime-context provider registry the loop evaluates at each pre-step.
/// </summary>
public sealed class AgentLoop : Service
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "agentLoop";

    private readonly int _maxParallelToolCalls;
    private readonly Dictionary<SessionId, LoopAgent> _loops = new();
    private readonly List<Func<Task<RuntimeContextPart>>> _contextProviders = new();

    /// <summary>
    /// Create the factory and register it as <c>agentLoop</c>. Registration also installs the
    /// loop-request invariant on the context's <c>llm/stream</c> waterfall.
    /// </summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <param name="config">the deployment-wide scheduler cap (see <see cref="AgentLoopConfig"/>).</param>
    public AgentLoop(Context ctx, AgentLoopConfig? config = null)
        : base(ctx, ServiceKey)
    {
        _maxParallelToolCalls = AgentLoopConfigResolver.ResolveMaxParallelToolCalls(config?.MaxParallelToolCalls);
        Ctx.Effect(() => AgentLoopInvariant.Install(Ctx), "agentLoop.invariant()");
        Ctx.On("agent/disposed", new Action<object?>(payload =>
        {
            if (payload is AgentDisposedPayload { Agent: var agent })
            {
                lock (_loops) _loops.Remove(agent.Id);
            }
        }));
    }

    /// <summary>The resolved deployment-wide scheduler cap.</summary>
    public int MaxParallelToolCalls => _maxParallelToolCalls;

    /// <summary>
    /// Create an agent and session under one caller-supplied identity and publish the running
    /// agent.
    /// </summary>
    /// <param name="id">the shared agent/session identity.</param>
    /// <param name="options">provider route, model, and token ceiling.</param>
    /// <param name="source">the session-start source ("startup" by default).</param>
    /// <returns>the published running agent's handle.</returns>
    public AgentHandle Create(SessionId id, AgentOptions? options = null, string source = "startup")
    {
        var runtime = LoopRuntime.Resolve(Ctx);
        var session = runtime.Sessions.Create(id);
        return Publish(runtime, id, options, session, source);
    }

    /// <summary>
    /// Resume an agent from the configured persistence service: the stored log rehydrates a live
    /// session under its exact identity and the agent republishes on it.
    /// </summary>
    /// <param name="id">the persisted session identity.</param>
    /// <param name="options">provider route, model, and token ceiling.</param>
    /// <param name="source">the session-start source ("resume" by default).</param>
    /// <returns>the published running agent's handle.</returns>
    /// <exception cref="InvalidOperationException">when persistence is not configured or the id has no stored log.</exception>
    public AgentHandle Resume(SessionId id, AgentOptions? options = null, string source = "resume")
    {
        var runtime = LoopRuntime.Resolve(Ctx);
        var persistence = Ctx.Get<SessionPersistenceService>("sessionPersistence")
            ?? throw new InvalidOperationException("cannot resume: session persistence is not configured (load a dsh-session-persistence backend)");
        var stored = persistence.Load(id)
            ?? throw new InvalidOperationException($"cannot resume: session \"{id}\" has no stored log");
        var session = runtime.Sessions.Create(id);
        session.Restore(stored.Events);
        return Publish(runtime, id, options, session, source);
    }

    /// <summary>The live driver of a published agent, or null when the agent is detached.</summary>
    public LoopAgent? GetLoop(SessionId id)
    {
        lock (_loops) return _loops.GetValueOrDefault(id);
    }

    /// <summary>
    /// Register one dynamic runtime-context provider; the loop evaluates every provider at each
    /// pre-step and projects a snapshot message when the joined text changes. The returned
    /// disposer removes the provider.
    /// </summary>
    public IDisposable RegisterContextProvider(Func<Task<RuntimeContextPart>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _contextProviders.Add(provider);
        return new LoopDisposable(() => _contextProviders.Remove(provider));
    }

    /// <summary>Construct the driver, enter the registries, announce, and notify session-start.</summary>
    private AgentHandle Publish(LoopRuntime runtime, SessionId id, AgentOptions? options, Dsh.Session.Session session, string source)
    {
        var registry = Ctx.Get<AgentRegistry>("agents")
            ?? throw new InvalidOperationException("agentLoop requires the \"agents\" registry");
        var agent = new Dsh.Agent.Agent(Ctx, session, options);
        var loop = new LoopAgent(Ctx, agent, runtime, _contextProviders);
        var handle = registry.Register(agent);
        lock (_loops) _loops[id] = loop;
        try
        {
            Ctx.Emit(LoopEvents.SessionStart, new SessionStartPayload(agent, source));
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"agent \"{id}\": agent/session-start listener threw: {error.Message}");
        }
        return handle;
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action (Cordis.Core's is internal).</summary>
internal sealed class LoopDisposable : IDisposable
{
    private readonly Action _action;

    public LoopDisposable(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
