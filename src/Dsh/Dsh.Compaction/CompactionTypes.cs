using Dsh.Session;

namespace Dsh.Compaction;

/// <summary>Opaque identity shared by one compaction transaction's durable markers (port of the TS CompactionId brand).</summary>
public readonly record struct CompactionId(string Value)
{
    public static implicit operator string(CompactionId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Default compaction policy fractions (port of the compaction-basic config defaults).</summary>
public static class CompactionPolicyDefaults
{
    /// <summary>Compact at this fraction of the model's context window.</summary>
    public const double ThresholdRatio = 0.8;

    /// <summary>Recent context retained as a fraction of the model's window.</summary>
    public const double RetainRatio = 0.16;

    /// <summary>Provider generation cap for summarization.</summary>
    public const long MaxTokens = 8192;
}

/// <summary>Loud failure validating compaction configuration (port of the compaction-basic config validators).</summary>
public sealed class CompactionConfigError : Exception
{
    /// <summary>Create the error with a configuration diagnostic.</summary>
    public CompactionConfigError(string message)
        : base(message)
    {
    }
}

/// <summary>Target-specific pressure/budget configuration failure (port of TargetPressureConfigError).</summary>
public sealed class TargetPressureConfigError : Exception
{
    /// <summary>Create the error; <paramref name="targetKey"/> is the exact provider/model route used as the warning key.</summary>
    public TargetPressureConfigError(string targetKey, string message)
        : base(message)
    {
        TargetKey = targetKey;
    }

    /// <summary>The exact provider/model route this budget belongs to.</summary>
    public string TargetKey { get; }
}

/// <summary>Stable failure classes for a compaction request (port of ManualCompactionErrorCode).</summary>
public enum ManualCompactionErrorCode
{
    /// <summary>Another compaction transaction already holds the durable lock.</summary>
    Busy,

    /// <summary>The request was cancelled.</summary>
    Cancelled,

    /// <summary>The selected span changed during the transaction.</summary>
    Changed,

    /// <summary>The summary was not smaller than the shadowed content.</summary>
    Summary,

    /// <summary>The commit stage failed.</summary>
    Commit,

    /// <summary>The durability checkpoint failed.</summary>
    Persistence,
}

/// <summary>Expected classified compaction failure (port of ManualCompactionError).</summary>
public sealed class ManualCompactionError : Exception
{
    /// <summary>Create the classified failure.</summary>
    public ManualCompactionError(ManualCompactionErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>The stable failure class.</summary>
    public ManualCompactionErrorCode Code { get; }
}

/// <summary>One validated inclusive span of current surface positions.</summary>
public sealed record SurfaceSelection(
    /// <summary>First surface-node seq, inclusive.</summary>
    long StartSeq,
    /// <summary>Last surface-node seq, inclusive.</summary>
    long EndSeq,
    /// <summary>Index of <see cref="StartSeq"/> in the current surface.</summary>
    int StartIndex,
    /// <summary>Index of <see cref="EndSeq"/> in the current surface.</summary>
    int EndIndex,
    /// <summary>The seqs of all shadowed surface nodes, in surface order.</summary>
    IReadOnlyList<long> ShadowedSeqs);

/// <summary>
/// Consumer compaction request. The provider/model name the summarization target (the port has no
/// conversation routing, so there is no separate conversation target). Budget fields mirror the TS
/// policy: an explicit <see cref="RetainTokens"/> replaces the ratio form; the resolve step turns
/// the request into concrete token budgets.
/// </summary>
public sealed record CompactionRequest(
    /// <summary>The session whose surface is compacted.</summary>
    Dsh.Session.Session Session,
    /// <summary>Adapter-owned model context capacity, a positive integer.</summary>
    long ContextWindow,
    /// <summary>Summarization provider route.</summary>
    string Provider = "local",
    /// <summary>Summarization model id.</summary>
    string Model = "basic",
    /// <summary>Compact at this fraction of the context window; defaults to <see cref="CompactionPolicyDefaults.ThresholdRatio"/>.</summary>
    double ThresholdRatio = CompactionPolicyDefaults.ThresholdRatio,
    /// <summary>Recent tail retained as a fraction of the context window; mutually exclusive with <see cref="RetainTokens"/>.</summary>
    double? RetainRatio = null,
    /// <summary>Absolute recent-tail token budget; mutually exclusive with <see cref="RetainRatio"/>.</summary>
    long? RetainTokens = null,
    /// <summary>Provider generation cap for summarization.</summary>
    long MaxTokens = CompactionPolicyDefaults.MaxTokens,
    /// <summary>Checkpoint text for the replacement user message; when omitted, the provider writes a deterministic placeholder.</summary>
    string? SummaryText = null);

/// <summary>
/// Resolved compaction budget and the selected region (port of ResolvedCompactSpec plus the
/// selectCompactableRange result). <see cref="Region"/> is null when no safe compactable range
/// exists under the resolved retention budget.
/// </summary>
public sealed record CompactionSpec(
    /// <summary>Adapter-owned model context capacity.</summary>
    long ContextWindow,
    /// <summary>Pressure fraction this spec was resolved from.</summary>
    double ThresholdRatio,
    /// <summary>Concrete pressure threshold: floor(contextWindow * thresholdRatio).</summary>
    long ThresholdTokens,
    /// <summary>Concrete recent-tail budget retained verbatim.</summary>
    long RetainTokens,
    /// <summary>Summarization provider route.</summary>
    string Provider,
    /// <summary>Summarization model id.</summary>
    string Model,
    /// <summary>Provider generation cap for summarization.</summary>
    long MaxTokens,
    /// <summary>The head-anchored region to compact, or null when nothing compactable remains.</summary>
    SurfaceSelection? Region);

/// <summary>Result of a completed compaction transaction.</summary>
public sealed record CompactionResult(
    /// <summary>Stable identity shared by this transaction's durable markers.</summary>
    CompactionId CompactionId,
    /// <summary>Seq of the appended compaction/start event.</summary>
    long StartSeq,
    /// <summary>Seq of the appended compaction/summary event.</summary>
    long SummarySeq,
    /// <summary>Seq of the appended checkpoint user message.</summary>
    long CheckpointSeq,
    /// <summary>Seq of the appended compaction/end event.</summary>
    long EndSeq,
    /// <summary>The checkpoint text written to the surface.</summary>
    string Summary,
    /// <summary>The shadowed surface span.</summary>
    SurfaceSelection Region,
    /// <summary>Estimated token count of the shadowed content.</summary>
    long ShadowedTokenCount);

/// <summary>
/// Surface projection helpers. The C# session has no separate surface object; the surface is the
/// derived message stream, so the node list is the seqs of events that derive a message, in log
/// order.
/// </summary>
public static class SessionSurface
{
    /// <summary>The current surface node seqs, in log order (events that derive a non-null message).</summary>
    public static IReadOnlyList<long> Nodes(Dsh.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var nodes = new List<long>();
        foreach (var evt in session.Events)
        {
            if (Dsh.Session.Surface.DeriveEventMessage(evt) is not null) nodes.Add(evt.Seq);
        }
        return nodes;
    }

    /// <summary>Index of a seq in the current surface, or -1 when absent.</summary>
    public static int IndexOf(Dsh.Session.Session session, long seq) => IndexOfSeq(Nodes(session), seq);

    /// <summary>Index of a value in a seq list, or -1 when absent.</summary>
    public static int IndexOfSeq(IReadOnlyList<long> seqs, long seq)
    {
        for (var i = 0; i < seqs.Count; i++)
        {
            if (seqs[i] == seq) return i;
        }
        return -1;
    }
}
