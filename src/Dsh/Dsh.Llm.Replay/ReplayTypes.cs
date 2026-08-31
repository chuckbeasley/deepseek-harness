using Dsh.Llm;

namespace Dsh.Llm.Replay;

/// <summary>One recorded model call in a replay script.</summary>
public abstract record ReplayEntry;

/// <summary>A successful recorded stream: the exact chunk sequence the live adapter emitted.</summary>
public sealed record ChunksEntry(IReadOnlyList<StreamChunk> Chunks) : ReplayEntry;

/// <summary>
/// A recorded provider failure: emit whatever the adapter streamed before it threw, then throw
/// the recorded error (e.g. a provider 401, or a mid-stream STREAM_CLOSED after partial chunks).
/// </summary>
public sealed record ThrowEntry(IReadOnlyList<StreamChunk> Chunks, string Message, string Code, bool? Accepted = null) : ReplayEntry;

/// <summary>A recorded stream that stalls until cancelled (mirrors the mock adapter).</summary>
public sealed record HangEntry(string? ReadyFile = null) : ReplayEntry;

/// <summary>Resolved replay plugin configuration; env-var defaulting happens at install time.</summary>
public sealed record ReplayConfig
{
    /// <summary>Path to the primary (parent) <c>session.jsonl</c> fixture.</summary>
    public required string File { get; init; }

    /// <summary>Optional sidecar for the primary session (a bare <see cref="ReplayEntry"/>[] or <c>{ patches }</c>).</summary>
    public string? OverrideFile { get; init; }

    /// <summary>Additional recorded child-session logs, ordered by <c>createdAt</c>.</summary>
    public IReadOnlyList<string>? ChildFiles { get; init; }

    /// <summary>The provider route to register the replay adapter under (defaults to <c>deepseek-official</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>Optional per-chunk pacing delay in milliseconds (a realism knob only).</summary>
    public int PaceMs { get; init; }
}

/// <summary>One recorded session's script plus the ordering facts used to bind parent and children.</summary>
public sealed record SessionScript(string RecordedId, long CreatedAtMs, IReadOnlyList<ReplayEntry> Entries, bool Primary);

/// <summary>One positional patch in an augmentation sidecar.</summary>
public sealed record ReplayOverridePatch(int At, ReplayEntry Entry);