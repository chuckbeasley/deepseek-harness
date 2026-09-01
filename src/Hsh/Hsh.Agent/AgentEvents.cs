using Harness.Llm;

namespace Harness.Agent;

/// <summary>
/// Event names and typed payload records for the live <c>agent/*</c> event set (port of the
/// scope-filtered agent events in the TS runtime-types). Events dispatch on the agent's owner
/// context with the payload record as the single argument; each payload carries the subject
/// <see cref="Agent"/>, so listeners can filter by identity.
/// </summary>
public static class AgentEvents
{
    /// <summary>A fully configured agent and live session were published.</summary>
    public const string Created = "agent/created";

    /// <summary>An agent left the registry; the registry emits this after detaching the entry.</summary>
    public const string Disposed = "agent/disposed";

    /// <summary>Agent status changed (<see cref="AgentStatus.Idle"/> toggles <see cref="AgentStatus.Running"/>).</summary>
    public const string Status = "agent/status";

    /// <summary>One message entered the live inbox.</summary>
    public const string InboxInserted = "agent/inbox/inserted";

    /// <summary>One message left the inbox inside its open turn.</summary>
    public const string InboxClaimed = "agent/inbox/claimed";

    /// <summary>One message was discarded from the live inbox.</summary>
    public const string InboxDiscarded = "agent/inbox/discarded";

    /// <summary>A step opened: one model request plus the tools it calls.</summary>
    public const string StepStart = "agent/step/start";

    /// <summary>A step closed.</summary>
    public const string StepEnd = "agent/step/end";
}

/// <summary>Common contract of every agent event payload: the subject agent.</summary>
public interface IAgentEventPayload
{
    /// <summary>The agent the event is about.</summary>
    Agent Agent { get; }
}

/// <summary>Payload of <see cref="AgentEvents.Created"/>.</summary>
public sealed record AgentCreatedPayload(Agent Agent) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.Disposed"/>.</summary>
public sealed record AgentDisposedPayload(Agent Agent) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.Status"/>: the status just entered.</summary>
public sealed record AgentStatusPayload(Agent Agent, AgentStatus Status) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.InboxInserted"/>: the inserted message.</summary>
public sealed record AgentInboxInsertedPayload(Agent Agent, UserMessage Message) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.InboxClaimed"/>: the claimed message and its owning turn.</summary>
public sealed record AgentInboxClaimedPayload(Agent Agent, UserMessage Message, long Turn) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.InboxDiscarded"/>: the discarded message.</summary>
public sealed record AgentInboxDiscardedPayload(Agent Agent, UserMessage Message) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.StepStart"/>: the step just opened.</summary>
public sealed record AgentStepStartPayload(Agent Agent, long Turn, long Step) : IAgentEventPayload;

/// <summary>Payload of <see cref="AgentEvents.StepEnd"/>: the step just closed.</summary>
public sealed record AgentStepEndPayload(Agent Agent, long Turn, long Step) : IAgentEventPayload;
