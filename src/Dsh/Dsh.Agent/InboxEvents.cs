using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Agent;

/// <summary>
/// One normalized mutation of an agent's durable pending-message lists (port of the TS
/// <c>agent/inbox/spliced</c>). Live dispatch precedes projection mutation, so synchronous
/// observers may read the pre-splice inbox to recover the removed messages.
/// </summary>
public sealed record InboxSplicedEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "agent/inbox/spliced";

    /// <summary>The pending list that mutated ("next-turn" or "next-step").</summary>
    public required string Target { get; init; }

    /// <summary>The normalized splice start.</summary>
    public required int Start { get; init; }

    /// <summary>Removed count; absent when the splice removed nothing.</summary>
    public int? RemovedCount { get; init; }

    /// <summary>The inserted messages.</summary>
    public required IReadOnlyList<UserMessage> Inserted { get; init; }

    /// <summary>Marks a cancellation-caused discard.</summary>
    public string? Outcome { get; init; }

    /// <inheritdoc />
    public override string Type => EventTypeName;
}

/// <summary>Register the agent-owned session event types (the plugin-boot equivalent of the TS event-type registration).</summary>
public static class AgentEventTypes
{
    /// <summary>Register the inbox splice marker; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(InboxSplicedEvent.EventTypeName, typeof(InboxSplicedEvent));
    }
}