namespace Dsh.Agent;

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
