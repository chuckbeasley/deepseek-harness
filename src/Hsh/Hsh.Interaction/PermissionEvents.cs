using Harness.Session;

namespace Harness.Interaction;

/// <summary>
/// The session's permission preset was applied (port of the TS <c>permission/preset</c>):
/// log-only, durable and replayable. The preset's sandbox mode and approval policy follow it.
/// </summary>
public sealed record PermissionPresetEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "permission/preset";

    /// <summary>The preset id in effect.</summary>
    public required string Preset { get; init; }

    /// <inheritdoc />
    public override string Type => EventTypeName;
}