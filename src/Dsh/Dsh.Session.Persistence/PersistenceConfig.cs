namespace Harness.Session.Persistence;

/// <summary>How the JSONL backend makes appended session events durable.</summary>
public enum FlushMode
{
    /// <summary>Flush every append to disk before the append returns (the default).</summary>
    SyncAppend,

    /// <summary>Buffer appends in memory and flush them together after a configured delay.</summary>
    Batched,
}

/// <summary>
/// Configuration for the JSONL session-persistence backend. No tunable is hardcoded: the log
/// location and the flush policy are deployment choices set here, each with a documented default.
/// </summary>
public sealed record PersistenceConfig
{
    /// <summary>
    /// Root directory holding one per-session directory per stored session. Required (no default):
    /// a default of the process working directory would scatter session files as the cwd changes.
    /// An absent root is created on first write.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Flush policy; defaults to <see cref="FlushMode.SyncAppend"/> so each append is durable
    /// before it returns. <see cref="FlushMode.Batched"/> coalesces writes until
    /// <see cref="BatchDelayMs"/> elapses or <see cref="SessionPersistenceService.Flush"/> runs.
    /// </summary>
    public FlushMode FlushMode { get; init; } = FlushMode.SyncAppend;

    /// <summary>
    /// Batched-flush interval in milliseconds; used only when <see cref="FlushMode"/> is
    /// <see cref="FlushMode.Batched"/>. Defaults to 100 ms. A background timer flushes pending
    /// writes when this interval elapses; disposal and <see cref="SessionPersistenceService.Flush"/>
    /// always flush immediately.
    /// </summary>
    public int BatchDelayMs { get; init; } = 100;
}
