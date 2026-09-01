namespace Dsh.AgentLoop;

/// <summary>
/// Event names and typed payload records the loop dispatches on the agent's owner context (port
/// of the TS loop dispatch surface). Waterfall events carry the subject agent in their proposal
/// so listeners can filter by identity, matching the scoped-listener convention in Dsh.Scope.
/// </summary>
public static class LoopEvents
{
    /// <summary>Propose one step's claimed messages; listeners may reject or rewrite them (waterfall).</summary>
    public const string PreStep = "agent/pre-step";

    /// <summary>Propose the next request's call config; listeners may override fields (waterfall).</summary>
    public const string Request = "agent/request";

    /// <summary>Propose a recovery action for a failed stream (waterfall; retry or stop).</summary>
    public const string RequestError = "agent/request-error";

    /// <summary>The turn is about to end with no next-step input left (observe-only emit).</summary>
    public const string TurnStopping = "agent/turn-stopping";

    /// <summary>A published agent entered its live session (emitted on create and resume).</summary>
    public const string SessionStart = "agent/session-start";

    /// <summary>One contained driver failure at its live boundary (observe-only emit).</summary>
    public const string Error = "agent/error";
}

/// <summary>Proposal delivered to <see cref="LoopEvents.PreStep"/> listeners.</summary>
public sealed record PreStepProposal(Dsh.Agent.Agent Agent, IReadOnlyList<UserMessage> Messages, long Turn, long Step);

/// <summary>The decision a pre-step waterfall listener returns.</summary>
public abstract record PreStepDecision
{
    /// <summary>"reject" or "enter".</summary>
    public abstract string Kind { get; }
}

/// <summary>Block the turn before any step; the loop logs <see cref="BlockedReason"/>.</summary>
public sealed record RejectDecision : PreStepDecision
{
    public static readonly RejectDecision Instance = new();

    public override string Kind => "reject";
}

/// <summary>Enter the step with these messages; the loop's default carries the claimed batch and the assembly.</summary>
public sealed record EnterDecision(IReadOnlyList<UserMessage> Messages, PromptAssembly? Assembly = null) : PreStepDecision
{
    public override string Kind => "enter";
}

/// <summary>Proposal delivered to <see cref="LoopEvents.Request"/> listeners.</summary>
public sealed record RequestProposal(Dsh.Agent.Agent Agent, long Turn, long Step, LlmCallConfig SeedConfig);

/// <summary>Proposal delivered to <see cref="LoopEvents.RequestError"/> listeners.</summary>
public sealed record RequestErrorProposal(
    Dsh.Agent.Agent Agent, long Turn, long Step, string Provider, LlmFailure Failure, CancellationToken CancellationToken);

/// <summary>The decision a request-error waterfall listener returns.</summary>
public abstract record RequestErrorAction
{
    /// <summary>"retry", "compaction", or "stop".</summary>
    public abstract string Kind { get; }
}

/// <summary>Retry the step's model call against the same durable header.</summary>
public sealed record RetryDecision : RequestErrorAction
{
    public static readonly RetryDecision Instance = new();

    public override string Kind => "retry";
}

/// <summary>Recover from a request failure by compacting the session, then retry as a new request series.</summary>
public sealed record CompactionDecision : RequestErrorAction
{
    public static readonly CompactionDecision Instance = new();

    public override string Kind => "compaction";
}

/// <summary>Do not retry; the loop surfaces the failure as the turn's error reason.</summary>
public sealed record StopDecision : RequestErrorAction
{
    public static readonly StopDecision Instance = new();

    public override string Kind => "stop";
}

/// <summary>Payload of <see cref="LoopEvents.TurnStopping"/>.</summary>
public sealed record TurnStoppingProposal(Dsh.Agent.Agent Agent, long Turn);

/// <summary>Payload of <see cref="LoopEvents.Error"/>.</summary>
public sealed record AgentErrorPayload(Dsh.Agent.Agent Agent, long Turn, long Step, Exception Error);

/// <summary>Payload of <see cref="LoopEvents.SessionStart"/>.</summary>
public sealed record SessionStartPayload(Dsh.Agent.Agent Agent, string Source);
