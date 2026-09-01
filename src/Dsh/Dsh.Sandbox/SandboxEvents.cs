using Harness.Session;

namespace Harness.Sandbox;

/// <summary>
/// The session's sandbox mode was set (port of the TS <c>sandbox/mode</c>): log-only, durable
/// and replayable, never in the model transcript. The LAST such event is the session's override;
/// an absent one leaves the deployment default in effect.
/// </summary>
public sealed record SandboxModeEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "sandbox/mode";

    /// <summary>The mode in effect until the next switch.</summary>
    public required SandboxMode Mode { get; init; }

    /// <summary>Marks an override seeded into a child at delegation.</summary>
    public string? Source { get; init; }

    /// <inheritdoc />
    public override string Type => EventTypeName;
}

/// <summary>Register the sandbox-owned session event types (idempotent per discriminator).</summary>
public static class SandboxEventTypes
{
    /// <summary>Register the sandbox-mode marker.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(SandboxModeEvent.EventTypeName, typeof(SandboxModeEvent));
    }
}