using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Compaction;

/// <summary>
/// Fixed local token estimator replacing the TS conversation token meter (dsh-token-meter is not
/// ported): every derived message node prices at ceil(UTF-16 chars / 4), minimum 1. The estimate is
/// deterministic and dependency-free; route-priced per-node token counts and the O(1) projection
/// fold are deferred with the meter service. The shadow-price protocol of the TS (fixed heuristic
/// price for replacements) uses these estimates.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Per-surface-node token estimates, aligned with <see cref="SessionSurface.Nodes"/>.</summary>
    public static IReadOnlyList<long> Estimate(Dsh.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var tokens = new List<long>();
        foreach (var evt in session.Events)
        {
            var message = Surface.DeriveEventMessage(evt);
            if (message is not null) tokens.Add(EstimateMessage(message));
        }
        return tokens;
    }

    /// <summary>The deterministic price of one derived message node.</summary>
    public static long EstimateMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        long chars = 0;
        CountChars(message.Content, ref chars);
        return Math.Max(1, (chars + 3) / 4);
    }

    private static void CountChars(IReadOnlyList<ContentBlock> blocks, ref long chars)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    chars += text.Text.Length;
                    break;
                case ToolCallBlock call:
                    chars += call.Name.Length + call.Arguments.Length;
                    break;
                case ToolResultBlock result:
                    CountChars(result.Content, ref chars);
                    break;
                // Reasoning and unknown blocks carry no model-visible text.
            }
        }
    }
}

/// <summary>
/// ctx.compaction: the basic deterministic compaction provider (port of dsh-compaction-basic).
/// The provider resolves a budget — head/tail trim within token budgets — and runs one durable
/// compaction transaction: it appends the compaction/start|summary|end marker pair and a
/// checkpoint user message. The retained tail is chosen by walking the surface from the tail until
/// the retain budget is met, then walking the cut left until it is both tool-pairing balanced and
/// turn-aligned, so a compaction never splits a turn or a tool-call/result pair.
///
/// Port decisions (each named): no token meter (fixed local estimator), no LLM summarizer (the
/// checkpoint text is a deterministic placeholder unless the request supplies one), no automatic
/// step-boundary/overflow listeners, no compactNow/runMaintenance, and — because the C# surface
/// has no replace op — the checkpoint is appended with source-event citations instead of replacing
/// the shadowed range in place. DEFERRED (named, not ported): command-compact and
/// compaction-tool-result-pruner.
/// </summary>
public sealed class BasicCompactionProvider : Service, ICompactionService
{
    private readonly Func<Dsh.Session.Session, IReadOnlyList<long>> _estimator;

    /// <summary>Create and register the service as <c>compaction</c>.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="estimator">injectable per-node token estimator; defaults to <see cref="TokenEstimator.Estimate"/>.</param>
    public BasicCompactionProvider(Context ctx, Func<Dsh.Session.Session, IReadOnlyList<long>>? estimator = null)
        : base(ctx, "compaction")
    {
        _estimator = estimator ?? TokenEstimator.Estimate;
        CompactionEventTypes.Register();
    }

    /// <summary>Read the compaction service from a context, failing explicitly when it is absent.</summary>
    public static BasicCompactionProvider Require(Context ctx) => ctx.Require<BasicCompactionProvider>("compaction");

    /// <inheritdoc />
    public CompactionSpec Resolve(CompactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Session);
        ValidatePolicy(request);
        var targetKey = $"{request.Provider}/{request.Model}";
        if (request.ContextWindow <= 0)
        {
            throw new TargetPressureConfigError(
                targetKey,
                $"compaction: contextWindow ({request.ContextWindow}) must be a positive integer");
        }
        var thresholdTokens = (long)Math.Floor(request.ContextWindow * request.ThresholdRatio);
        var retainTokens = request.RetainTokens
            ?? (long)Math.Floor(request.ContextWindow * (request.RetainRatio ?? CompactionPolicyDefaults.RetainRatio));
        if (retainTokens >= thresholdTokens)
        {
            throw new TargetPressureConfigError(
                targetKey,
                $"compaction: {request.Provider}/{request.Model} retainTokens ({retainTokens}) must be less than threshold tokens {thresholdTokens}");
        }
        var region = SelectRange(request.Session, retainTokens);
        return new CompactionSpec(
            request.ContextWindow,
            request.ThresholdRatio,
            thresholdTokens,
            retainTokens,
            request.Provider,
            request.Model,
            request.MaxTokens,
            region);
    }

    /// <inheritdoc />
    public CompactionResult? Compact(CompactionRequest request)
    {
        var spec = Resolve(request);
        if (spec.Region is null) return null;
        return Run(request, spec);
    }

    /// <inheritdoc />
    public SurfaceSelection? SelectRange(Dsh.Session.Session session, long retainTokens)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (retainTokens < 0)
        {
            throw new ArgumentException("retainTokens must be a non-negative integer", nameof(retainTokens));
        }
        var nodes = SessionSurface.Nodes(session);
        if (nodes.Count == 0) return null;
        var priced = _estimator(session);
        if (priced.Count != nodes.Count)
        {
            throw new InvalidOperationException("compaction: token estimator surface does not match the current session surface");
        }

        // Walk from the tail, accumulating tokens until the retain budget is met; everything
        // before that point is a candidate head (the port of selectCompactableRange).
        long accumulated = 0;
        var keepFromIdx = nodes.Count;
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            accumulated += priced[index];
            keepFromIdx = index;
            if (accumulated >= retainTokens) break;
        }
        if (keepFromIdx == 0) return null;

        // Walk the cut left until it is both tool-pairing balanced and turn-aligned, so the
        // compacted head contains whole turns and never splits a tool-call/result pair.
        while (keepFromIdx > 0)
        {
            if (TurnAlignment.IsTurnBoundaryCut(session, nodes[keepFromIdx])
                && ToolPairing.BalancedBefore(session, nodes[keepFromIdx]))
            {
                break;
            }
            keepFromIdx--;
        }
        if (keepFromIdx == 0) return null;

        var shadowed = new List<long>(keepFromIdx);
        for (var i = 0; i < keepFromIdx; i++) shadowed.Add(nodes[i]);
        return new SurfaceSelection(nodes[0], nodes[keepFromIdx - 1], 0, keepFromIdx - 1, shadowed);
    }

    /// <summary>Run the durable transaction for an already-resolved request and spec.</summary>
    private CompactionResult Run(CompactionRequest request, CompactionSpec spec)
    {
        var session = request.Session;
        var region = spec.Region!;
        AssertCompactionInactive(session);
        var compactionId = new CompactionId(Guid.NewGuid().ToString("N"));
        var turn = OpenTurn(session);

        var startEvent = session.Append(new CompactionStartEvent { CompactionId = compactionId, Turn = turn });
        var shadowedTokenCount = ShadowedTokenCount(session, region);
        var summary = request.SummaryText ?? DefaultCheckpointText(region, spec, shadowedTokenCount);
        var summaryEvent = session.Append(new CompactionSummaryEvent
        {
            CompactionId = compactionId,
            Summary = summary,
            RangeStart = region.StartSeq,
            RangeEnd = region.EndSeq,
            ShadowedSeqs = region.ShadowedSeqs,
            ShadowedTokenCount = shadowedTokenCount,
            Provider = spec.Provider,
            Model = spec.Model,
            MaxTokens = spec.MaxTokens,
        });
        // The replacement shadows the compacted range; the C# surface has no replace op, so the
        // checkpoint is appended with the transaction and range cited in SourceEventSeqs.
        var checkpointEvent = session.Append(new UserMessageEvent
        {
            Message = Messages.CreateUserMessage(
                new ContentBlock[] { new TextBlock(summary) },
                new PluginSource { Plugin = "compaction", Form = "checkpoint" }),
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new[] { startEvent.Seq, summaryEvent.Seq }.Concat(region.ShadowedSeqs).ToArray(),
        });
        var endEvent = session.Append(new CompactionEndEvent { CompactionId = compactionId, Turn = turn });

        return new CompactionResult(
            compactionId,
            startEvent.Seq,
            summaryEvent.Seq,
            checkpointEvent.Seq,
            endEvent.Seq,
            summary,
            region,
            shadowedTokenCount);
    }

    /// <summary>Sum the estimator's per-node prices over the shadowed span.</summary>
    private long ShadowedTokenCount(Dsh.Session.Session session, SurfaceSelection region)
    {
        var priced = _estimator(session);
        long total = 0;
        for (var i = region.StartIndex; i <= region.EndIndex; i++) total += priced[i];
        return total;
    }

    /// <summary>The deterministic placeholder checkpoint when the request supplies no summary.</summary>
    private static string DefaultCheckpointText(SurfaceSelection region, CompactionSpec spec, long shadowedTokenCount)
        => "This is an automatically generated checkpoint condensing an earlier span of the conversation to free up context.\n\n"
         + "<compacted-summary>\n"
         + $"Compacted {region.ShadowedSeqs.Count} surface nodes (seqs {region.StartSeq}-{region.EndSeq}, ~{shadowedTokenCount} tokens) by the {spec.Provider}/{spec.Model} basic provider.\n"
         + "</compacted-summary>";

    /// <summary>Reject a durable unmatched compaction marker: the lock is held until one compaction/end.</summary>
    private static void AssertCompactionInactive(Dsh.Session.Session session)
    {
        foreach (var evt in session.Events.Reverse())
        {
            if (evt is CompactionEndEvent) return;
            if (evt is CompactionStartEvent)
            {
                throw new ManualCompactionError(
                    ManualCompactionErrorCode.Busy,
                    "compaction: compaction already in progress; the session compaction lock is already active");
            }
        }
    }

    /// <summary>The open turn owning the next compaction, or null when no turn is open.</summary>
    private static long? OpenTurn(Dsh.Session.Session session)
    {
        foreach (var evt in session.Events.Reverse())
        {
            if (evt is TurnStartEvent start) return start.Turn;
            if (evt is TurnEndEvent) return null;
        }
        return null;
    }

    private static void ValidatePolicy(CompactionRequest request)
    {
        AssertRatio(nameof(request.ThresholdRatio), request.ThresholdRatio);
        if (request.RetainRatio is { } retainRatio)
        {
            AssertRatio(nameof(request.RetainRatio), retainRatio);
            if (retainRatio >= request.ThresholdRatio)
            {
                throw new CompactionConfigError(
                    $"compaction: retainRatio ({retainRatio}) must be less than the resolved thresholdRatio ({request.ThresholdRatio})");
            }
        }
        if (request.RetainTokens is { } retainTokens && retainTokens < 0)
        {
            throw new CompactionConfigError($"compaction: retainTokens ({retainTokens}) must be a non-negative integer");
        }
        if (request.RetainRatio is not null && request.RetainTokens is not null)
        {
            throw new CompactionConfigError("compaction: retainRatio and retainTokens are mutually exclusive");
        }
        if (string.IsNullOrEmpty(request.Provider) || string.IsNullOrEmpty(request.Model))
        {
            throw new CompactionConfigError("compaction: provider and model must be non-empty strings");
        }
        if (request.MaxTokens <= 0)
        {
            throw new CompactionConfigError($"compaction: maxTokens ({request.MaxTokens}) must be a positive integer");
        }
    }

    private static void AssertRatio(string name, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 1)
        {
            throw new CompactionConfigError($"compaction: {name} ({value}) must be a number in (0, 1]");
        }
    }
}
