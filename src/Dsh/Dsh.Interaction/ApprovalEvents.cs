using Dsh.Session;

namespace Dsh.Interaction;

/// <summary>
/// Durable audit events for the approval seam (port of the TS <c>approval/asked</c> +
/// <c>approval/decided</c> pair and the <c>approval/policy</c> override). They are log-only: they
/// never join the model surface. The asked/decided pair is turn-enclosed by
/// <see cref="ApprovalService.AskAsync"/> — the turn is the durable log's commit/replay boundary,
/// so a bare event between turns would be crash-tail garbage on reload.
/// </summary>
public sealed record ApprovalAskedEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "approval/asked";

    /// <summary>The ask identity both audit events share.</summary>
    public required string Id { get; init; }

    /// <summary>The tool the question is about.</summary>
    public required string ToolName { get; init; }

    /// <summary>The exact tool call being decided, when the asker has one.</summary>
    public string? CallId { get; init; }

    /// <summary>The asker's human-readable explanation of why it is asking.</summary>
    public string? Reason { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>The closed outcome of the audit pair's ask.</summary>
public sealed record ApprovalDecidedEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "approval/decided";

    /// <summary>The ask identity both audit events share.</summary>
    public required string Id { get; init; }

    /// <summary>The closed outcome; <see cref="ApprovalOutcome.AllowedOnce"/> is the only grant.</summary>
    public required ApprovalOutcome Outcome { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>
/// The session's approval-policy override (port of the TS <c>approval/policy</c>): the LAST such
/// event is the session's override; an absent one leaves the configured default in effect. The
/// policy is never writable from the preset metadata — it is a runtime switch.
/// </summary>
public sealed record ApprovalPolicyEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "approval/policy";

    /// <summary>The policy in effect until the next switch.</summary>
    public required ApprovalPolicy Policy { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the interaction/* event types into the session registry (the plugin-boot equivalent of the TS event-type registration).</summary>
public static class InteractionEventTypes
{
    /// <summary>Register all three markers; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(ApprovalAskedEvent.EventTypeName, typeof(ApprovalAskedEvent));
        SessionEventTypes.Register(ApprovalDecidedEvent.EventTypeName, typeof(ApprovalDecidedEvent));
        SessionEventTypes.Register(ApprovalPolicyEvent.EventTypeName, typeof(ApprovalPolicyEvent));
        SessionEventTypes.Register(PermissionPresetEvent.EventTypeName, typeof(PermissionPresetEvent));
    }
}
