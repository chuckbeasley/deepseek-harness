using Dsh.Session;

namespace Dsh.Compaction;

/// <summary>
/// Plugin-merged session events recording the compaction transaction (port of the declaration-merged
/// compaction/* events). They are log-only: they never join the model surface, and the replacement
/// user message that shadows the compacted range carries the checkpoint text. The C# surface has no
/// replace op yet, so the checkpoint is appended with source-event citations instead of replacing
/// the range in place (documented port decision).
/// </summary>
public sealed record CompactionStartEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "compaction/start";

    /// <summary>The owning transaction identity.</summary>
    public required string CompactionId { get; init; }

    /// <summary>Numbered owner when the transaction is enclosed by an open turn; null for a standalone bracket.</summary>
    public long? Turn { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Completed summary, its inputs, and its shadowed range — log-only.</summary>
public sealed record CompactionSummaryEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "compaction/summary";

    /// <summary>The owning transaction identity.</summary>
    public required string CompactionId { get; init; }

    /// <summary>The checkpoint text written to the surface.</summary>
    public required string Summary { get; init; }

    /// <summary>First shadowed surface-node seq.</summary>
    public required long RangeStart { get; init; }

    /// <summary>Last shadowed surface-node seq.</summary>
    public required long RangeEnd { get; init; }

    /// <summary>The seqs of all shadowed surface nodes, in surface order.</summary>
    public required IReadOnlyList<long> ShadowedSeqs { get; init; }

    /// <summary>Estimated token count of the shadowed content.</summary>
    public required long ShadowedTokenCount { get; init; }

    /// <summary>The provider route that wrote the summary.</summary>
    public required string Provider { get; init; }

    /// <summary>The model that wrote the summary.</summary>
    public required string Model { get; init; }

    /// <summary>The generation cap the summarize call sent.</summary>
    public long? MaxTokens { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Marks the end of a compaction — log-only, releases the durable lock.</summary>
public sealed record CompactionEndEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "compaction/end";

    /// <summary>The owning transaction identity.</summary>
    public required string CompactionId { get; init; }

    /// <summary>Numbered owner matching the paired compaction/start; null for a standalone bracket.</summary>
    public long? Turn { get; init; }

    /// <summary>Failure detail when the attempt did not close cleanly.</summary>
    public string? Error { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the compaction/* event types into the session registry (the plugin-boot equivalent of the TS event-type registration).</summary>
public static class CompactionEventTypes
{
    /// <summary>Register all three markers; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(CompactionStartEvent.EventTypeName, typeof(CompactionStartEvent));
        SessionEventTypes.Register(CompactionSummaryEvent.EventTypeName, typeof(CompactionSummaryEvent));
        SessionEventTypes.Register(CompactionEndEvent.EventTypeName, typeof(CompactionEndEvent));
    }
}
