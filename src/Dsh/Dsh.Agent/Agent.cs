using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Agent;

/// <summary>
/// A live agent: session-backed identity, the two-list inbox, lifecycle status, step counters,
/// an agent-scoped context, and a cancellation signal (port of the TS Agent runtime face, minus
/// the loop-owned drive API). The loop (Dsh.AgentLoop) drives status, steps, and inbox claims;
/// everything here is the state and event surface the loop and observers share.
/// </summary>
public sealed class Agent : IInboxNotifications
{
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly Context _owner;
    private readonly Context _scope;
    private bool _scopeDisposed;

    /// <summary>
    /// Create a live agent on <paramref name="session"/>.
    /// </summary>
    /// <param name="owner">the context the agent dispatches its <c>agent/*</c> events through and
    /// that the registry lives on; register the agent through this same context.</param>
    /// <param name="session">the live session this agent drives; its log is the durable source of truth.</param>
    /// <param name="options">provider route, model, and token ceiling for this agent's requests.</param>
    /// <param name="config">deployment-varying limits; absent fields take their documented defaults.</param>
    public Agent(Context owner, Dsh.Session.Session session, AgentOptions? options = null, AgentConfig? config = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Options = options ?? new AgentOptions();
        Config = config ?? new AgentConfig();
        _scope = new Context();
        Inbox = new Inbox(this, Config.MaxPendingMessages);
    }

    /// <summary>The session-backed agent identity (the shared agent/session id).</summary>
    public SessionId Id => Session.Id;

    /// <summary>The context this agent dispatches its live events through.</summary>
    public Context Owner => _owner;

    /// <summary>
    /// The agent-scoped context: effects, services, and listeners registered through it unwind when
    /// the agent's scope disposes (see Dsh.Scope for the scoped-registration primitive).
    /// </summary>
    public Context Ctx => _scope;

    /// <summary>The live session this agent drives; its log is the durable source of truth.</summary>
    public Dsh.Session.Session Session { get; }

    /// <summary>The agent-owned projection of durable pending work.</summary>
    public Inbox Inbox { get; }

    /// <summary>The provider route and model this agent's requests use.</summary>
    public AgentOptions Options { get; }

    /// <summary>Deployment-varying limits for this agent.</summary>
    public AgentConfig Config { get; }

    /// <summary>
    /// The current lifecycle state, mirrored on every <c>agent/status</c> transition. Disposal
    /// removes the agent from its registry; it is not a third observable status.
    /// </summary>
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    /// <summary>The open turn number, or 0 before the first turn.</summary>
    public long Turn { get; private set; }

    /// <summary>The open step number, or 0 before the first step.</summary>
    public long Step { get; private set; }

    /// <summary>The lifecycle cancellation signal; <see cref="Cancel"/> triggers it.</summary>
    public CancellationToken CancellationToken => _lifecycle.Token;

    /// <summary>The first cause passed to <see cref="Cancel"/>, or null while no cancellation is active.</summary>
    public TurnEndCancelCause? LastCancelCause { get; private set; }

    /// <summary>
    /// Transition the lifecycle status and emit <c>agent/status</c> with the entered status.
    /// A no-op (and no event) when the status is unchanged.
    /// </summary>
    public void SetStatus(AgentStatus status)
    {
        if (status == Status) return;
        Status = status;
        EmitContained(AgentEvents.Status, new AgentStatusPayload(this, status));
    }

    /// <summary>
    /// Open step <paramref name="step"/> of turn <paramref name="turn"/>: record both numbers and
    /// emit <c>agent/step/start</c>. One step is one model request plus the tools it calls.
    /// The loop passes the per-turn step number; steps restart at 1 for each turn.
    /// </summary>
    public void StartStep(long turn, long step)
    {
        Turn = turn;
        Step = step;
        EmitContained(AgentEvents.StepStart, new AgentStepStartPayload(this, Turn, Step));
    }

    /// <summary>Open the next step of <paramref name="turn"/> and emit <c>agent/step/start</c>.</summary>
    public void StartStep(long turn) => StartStep(turn, Step + 1);

    /// <summary>Close the open step and emit <c>agent/step/end</c>.</summary>
    public void EndStep()
    {
        EmitContained(AgentEvents.StepEnd, new AgentStepEndPayload(this, Turn, Step));
    }

    /// <summary>
    /// Abort the active activity. The first cause wins; a later call without a cause keeps it.
    /// With no active activity, cancellation is a no-op that does not arm later work beyond this
    /// agent's lifecycle.
    /// </summary>
    /// <param name="cause">the stable caller intent carried by the cancellation signal.</param>
    public void Cancel(TurnEndCancelCause? cause = null)
    {
        LastCancelCause ??= cause ?? new UserCancel();
        // Cancellation is moot once the scope unwound: DisposeScope disposed the signal.
        if (_scopeDisposed) return;
        _lifecycle.Cancel();
    }

    /// <summary>
    /// Unwind the agent's scoped world and release its cancellation signal. Idempotent; the
    /// registry's detach path runs this after emitting <c>agent/disposed</c>.
    /// </summary>
    internal void DisposeScope()
    {
        if (_scopeDisposed) return;
        _scopeDisposed = true;
        _lifecycle.Cancel();
        _lifecycle.Dispose();
        _scope.Dispose();
    }

    /// <summary>Emit one agent event through the owner context, containing listener failures.</summary>
    internal void EmitContained(string name, IAgentEventPayload payload)
    {
        try
        {
            _owner.Emit(name, payload);
        }
        catch (Exception error)
        {
            _owner.Logger.Warn($"agent \"{Id}\": {name} listener threw: {error.Message}");
        }
    }

    void IInboxNotifications.Inserted(UserMessage message)
        => EmitContained(AgentEvents.InboxInserted, new AgentInboxInsertedPayload(this, message));

    void IInboxNotifications.Discarded(UserMessage message)
        => EmitContained(AgentEvents.InboxDiscarded, new AgentInboxDiscardedPayload(this, message));

    void IInboxNotifications.Claimed(UserMessage message, long turn)
        => EmitContained(AgentEvents.InboxClaimed, new AgentInboxClaimedPayload(this, message, turn));
}
