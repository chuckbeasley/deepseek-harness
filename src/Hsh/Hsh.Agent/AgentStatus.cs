namespace Harness.Agent;

/// <summary>
/// An agent's lifecycle state, emitted on every transition as <c>agent/status</c> (port of the TS
/// AgentStatus). <see cref="Idle"/> means no driver is active; <see cref="Running"/> begins when
/// waking input starts cancellable processing and lasts while the driver drains, closes, or
/// checkpoints turns. Disposal removes the agent from its registry; it is not a third observable
/// status.
/// </summary>
public enum AgentStatus
{
    /// <summary>No driver is active.</summary>
    Idle,

    /// <summary>A driver is processing or draining.</summary>
    Running,
}

/// <summary>
/// Agent creation options (port of AgentOptions): the provider route, model id, and per-request
/// output-token ceiling. Each field is interpreted by the adapter selected at call time.
/// </summary>
public sealed record AgentOptions
{
    /// <summary>Provider route (must have a registered adapter at call time).</summary>
    public string? Provider { get; init; }

    /// <summary>Model id interpreted by the selected provider adapter.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum output tokens for each conversation-model request.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The session's workspace directory; <c>null</c> defaults to the process cwd.</summary>
    public string? Cwd { get; init; }

    /// <summary>The session's subagent delegation depth (0 for a top-level session).</summary>
    public int DelegationDepth { get; init; }

    /// <summary>The parent session id for a subagent child session.</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>The child-session origin (e.g. "subagent"); absent for a top-level session.</summary>
    public string? Origin { get; init; }

    /// <summary>
    /// The named subagent provider a structured child runs under (e.g. "spawn"); the provider
    /// marker drives the recorded <c>subagent/descriptor</c> event in the child session.
    /// </summary>
    public string? SubagentProvider { get; init; }
}

/// <summary>
/// Deployment-varying agent limits (no hardcoded tunables). Fields are validated at the
/// configuration boundary; the defaults documented here apply when a field is absent.
/// </summary>
public sealed record AgentConfig
{
    /// <summary>
    /// Cap on pending messages held by one inbox list; null (the default) means unlimited.
    /// A configured cap rejects an insertion that would exceed it.
    /// </summary>
    public int? MaxPendingMessages { get; init; }
}
