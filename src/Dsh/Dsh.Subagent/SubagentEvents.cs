using Dsh.Session;

namespace Dsh.Subagent;

/// <summary>
/// The durable descriptor of a structured child session (port of the TS
/// <c>subagent/descriptor</c> event): one-shot children spawned by a structured provider carry
/// the provider name and the recorded descriptor version. Log-only; appended before the child's
/// first step/start.
/// </summary>
public sealed record SubagentDescriptorEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "subagent/descriptor";

    /// <summary>The recorded descriptor version.</summary>
    public required int Version { get; init; }

    /// <summary>The child lifecycle mode ("one-shot" for a single-turn structured child).</summary>
    public required string Mode { get; init; }

    /// <summary>The named provider that spawned the child (e.g. "spawn").</summary>
    public required string Provider { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the subagent/* event types into the session registry (the plugin-boot equivalent of the TS event-type registration).</summary>
public static class SubagentEventTypes
{
    /// <summary>Register the descriptor discriminator; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(SubagentDescriptorEvent.EventTypeName, typeof(SubagentDescriptorEvent));
    }
}