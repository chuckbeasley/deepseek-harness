using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Cordis.Core;
using Harness.AgentLoop;
using Harness.Llm;
using Harness.Session;

namespace Harness.Compaction;

/// <summary>
/// Fixed-density heuristic token pricing (port of hsh-token-meter/estimate: the fixed
/// CHARS_PER_TOKEN=4 density, per-block structural overhead 4, and role-framing overhead 4 that
/// the recorded compaction fixtures price with). The estimate is deterministic and
/// dependency-free; route-priced per-node token counts and the O(1) projection fold are deferred
/// with the meter service. The shadow-price protocol of the TS (fixed heuristic price for
/// replacements) uses these estimates.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Fixed text-density estimate used until exact tokenization is needed.</summary>
    private const int CharsPerToken = 4;

    /// <summary>Per-block structural overhead for JSON framing and type tags.</summary>
    private const int BlockOverhead = 4;

    /// <summary>Role-field framing overhead added to every priced message.</summary>
    private const int RoleOverhead = 4;

    private static readonly JsonSerializerOptions StructuralJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Per-surface-node token estimates, aligned with <see cref="SessionSurface.Nodes"/>.</summary>
    public static IReadOnlyList<long> Estimate(Harness.Session.Session session)
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

    /// <summary>The deterministic price of one derived message node (the TS estimateMessage).</summary>
    public static long EstimateMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return EstimateContent(message.Content) + RoleOverhead;
    }

    private static long EstimateContent(IReadOnlyList<ContentBlock> blocks)
    {
        long tokens = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    tokens += Ceil(text.Text.Length) + BlockOverhead;
                    break;
                case ReasoningBlock reasoning:
                    tokens += Ceil(reasoning.Text.Length) + BlockOverhead;
                    break;
                case ToolCallBlock call:
                    tokens += Ceil(call.Name.Length) + Ceil(call.Arguments.Length) + BlockOverhead;
                    break;
                case ToolResultBlock result:
                    tokens += EstimateContent(result.Content) + BlockOverhead;
                    break;
                // ContentBlock is merge-extensible; unknown blocks (and image references, whose
                // request price is route-owned) retain a conservative structural JSON price.
                default:
                    tokens += BlockOverhead + Ceil(JsonSerializer.Serialize(block, block.GetType(), StructuralJson).Length);
                    break;
            }
        }
        return tokens;
    }

    private static long Ceil(int length) => (length + CharsPerToken - 1) / CharsPerToken;
}

/// <summary>
/// ctx.compaction: the basic deterministic compaction provider (port of hsh-compaction-basic).
/// The provider resolves a budget — head/tail trim within token budgets — and runs one durable
/// compaction transaction: compaction/start|summary|end markers, a checkpoint user message whose
/// replace surface op shadows the compacted range in place, and a real one-shot
/// <c>llm/stream</c> summarization call (purpose <c>"compaction"</c>, the recorded
/// replay-servable call). The retained tail is chosen by walking the surface from the tail until
/// the retain budget is met, then walking the cut left until it is tool-pairing balanced (the TS
/// <c>selectCompactableRange</c>; no turn alignment). When <c>auto</c> is enabled, a
/// request-error listener recovers <c>CONTEXT_WINDOW_EXCEEDED</c> failures by compacting with a
/// zero retain budget and asking the loop to retry as a new request series.
///
/// Port decisions (each named): no token meter (fixed local estimator), overflow retries capped
/// per agent like the TS and reset on a successful assistant message, no pre-step pressure
/// listener (the corpus drives overflow only), no compactNow/runMaintenance, and no tool-result
/// pruner. DEFERRED (named, not ported): command-compact, compaction-tool-result-pruner, and the
/// pressure-triggered pre-step listener.
/// </summary>
public sealed class BasicCompactionProvider : Service, ICompactionService
{
    /// <summary>The provider-confirmed context-overflow failure code that triggers recovery.</summary>
    public const string ContextWindowExceededCode = "CONTEXT_WINDOW_EXCEEDED";

    /// <summary>Default per-agent overflow-recovery cap (the TS maxOverflowRetries default).</summary>
    public const int DefaultMaxOverflowRetries = 1;

    /// <summary>Tags wrapping the structured summary inside the landed checkpoint node.</summary>
    private const string SummaryOpenTag = "<compacted-summary>";

    private const string SummaryCloseTag = "</compacted-summary>";

    /// <summary>Framing that makes the replacement user message established context.</summary>
    private const string CheckpointPreamble =
        "This is an automatically generated checkpoint condensing an earlier span of the conversation to free up context. "
        + "Treat the captured context as established background and build on it without restating it. "
        + "Continue the task directly from the messages that follow, without acknowledging this checkpoint.";

    /// <summary>
    /// The summarization directive, delivered as the FINAL user message after the replayed
    /// conversation rather than as a distinct summarizer system prompt (verbatim port of the TS
    /// COMPACTION_INSTRUCTION).
    /// </summary>
    private const string CompactionInstruction =
        "You are now acting as a compaction engine for this AI coding assistant. Condense the conversation ABOVE into a structured checkpoint that lets another model resume the work with no loss of essential context.\n"
        + "\n"
        + "Output EXACTLY the Markdown structure below: keep every section, in order. Use terse bullets, not prose paragraphs. Write \"(none)\" for an empty section — never drop a section.\n"
        + "\n"
        + "## Primary Request and Intent\n"
        + "- [the user's original and evolving goals; quote verbatim where the exact wording matters]\n"
        + "\n"
        + "## Key Technical Concepts\n"
        + "- [technologies, frameworks, patterns, and conventions in play]\n"
        + "\n"
        + "## Files and Code\n"
        + "- [exact path: why it matters, key changes or snippets]\n"
        + "\n"
        + "## Errors and Fixes\n"
        + "- [error: how it was resolved, plus any related user feedback]\n"
        + "\n"
        + "## Pending Jobs\n"
        + "- [explicitly requested work not yet completed]\n"
        + "\n"
        + "## Current Work\n"
        + "- [precisely what was in progress at this checkpoint]\n"
        + "\n"
        + "## Next Step\n"
        + "- [the single next action, directly in line with the most recent request, or \"(none)\"]\n"
        + "\n"
        + "## Critical Context\n"
        + "- [decisions and their rationale, constraints, user preferences, open questions, data needed to continue]\n"
        + "\n"
        + "Rules:\n"
        + "- Write concise English engineering prose. Preserve exact file paths, commands, error strings, identifiers, numeric values, function signatures, and syntax fragments.\n"
        + "- Capture user feedback and explicit instructions faithfully, especially corrections.\n"
        + "- Do NOT mention this summarization request or that the context was compacted.\n"
        + "- Output only the checkpoint text: do not call any tool or take any other action.\n"
        + "- If the conversation already contains a <compacted-summary> block, it is a PRIOR checkpoint. Do not copy it forward verbatim: preserve still-true facts, drop stale ones, and merge newer information into a single consolidated summary under the same structure.";

    private readonly Func<Harness.Session.Session, IReadOnlyList<long>> _estimator;
    private readonly Func<SummarizationInput, CancellationToken, Task<SummaryResult>> _summarizer;
    private readonly int _maxTokens;
    private readonly int _maxOverflowRetries;
    private readonly Dictionary<string, int> _overflowRetries = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _listeners = new();

    /// <summary>
    /// Create and register the service as <c>compaction</c>. When <paramref name="auto"/> is set,
    /// the provider installs the context-overflow recovery listener on the loop's request-error
    /// waterfall.
    /// </summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="estimator">injectable per-node token estimator; defaults to <see cref="TokenEstimator.Estimate"/>.</param>
    /// <param name="summarizer">injectable summarizer; defaults to the one-shot <c>llm/stream</c> call.</param>
    /// <param name="maxTokens">provider generation cap for summarization.</param>
    /// <param name="maxOverflowRetries">per-agent overflow-recovery retry cap.</param>
    /// <param name="auto">whether the overflow recovery listener is installed.</param>
    public BasicCompactionProvider(
        Context ctx,
        Func<Harness.Session.Session, IReadOnlyList<long>>? estimator = null,
        Func<SummarizationInput, CancellationToken, Task<SummaryResult>>? summarizer = null,
        int maxTokens = (int)CompactionPolicyDefaults.MaxTokens,
        int maxOverflowRetries = DefaultMaxOverflowRetries,
        bool auto = true)
        : base(ctx, "compaction")
    {
        _estimator = estimator ?? TokenEstimator.Estimate;
        _summarizer = summarizer ?? SummarizeWithLlm;
        _maxTokens = maxTokens;
        _maxOverflowRetries = maxOverflowRetries;
        CompactionEventTypes.Register();
        if (auto) RegisterOverflowListener();
    }

    /// <inheritdoc />
    public override ValueTask StopAsync()
    {
        foreach (var listener in _listeners) listener.Dispose();
        _listeners.Clear();
        return base.StopAsync();
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
        return RunAsync(request, spec.Region, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Force one useful overflow recovery: select the head-anchored range with a zero retain
    /// budget (the TS context-overflow trigger) and run the durable transaction over it.
    /// </summary>
    /// <returns>the durable result, or null when no safe useful range exists.</returns>
    public async Task<CompactionResult?> CompactOverflowAsync(CompactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var region = SelectRange(request.Session, retainTokens: 0);
        if (region is null) return null;
        return await RunAsync(request, region, ct);
    }

    /// <inheritdoc />
    public SurfaceSelection? SelectRange(Harness.Session.Session session, long retainTokens)
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

        // Walk the cut left until it is tool-pairing balanced, so the compacted head never
        // splits a tool-call/result pair (the TS rule; no turn alignment).
        while (keepFromIdx > 0)
        {
            if (ToolPairing.BalancedBefore(session, nodes[keepFromIdx])) break;
            keepFromIdx--;
        }
        if (keepFromIdx == 0) return null;

        var shadowed = new List<long>(keepFromIdx);
        for (var i = 0; i < keepFromIdx; i++) shadowed.Add(nodes[i]);
        return new SurfaceSelection(nodes[0], nodes[keepFromIdx - 1], 0, keepFromIdx - 1, shadowed);
    }

    /// <summary>Run the durable transaction for an already-resolved request and selected region.</summary>
    private async Task<CompactionResult> RunAsync(CompactionRequest request, SurfaceSelection region, CancellationToken ct)
    {
        var session = request.Session;
        AssertCompactionInactive(session);
        var compactionId = new CompactionId(Guid.NewGuid().ToString("D"));
        var turn = OpenTurn(session);

        var startEvent = session.Append(new CompactionStartEvent { CompactionId = compactionId.Value, Turn = turn });
        var shadowedTokenCount = ShadowedTokenCount(session, region);
        var summarized = await _summarizer(BuildSummarizationInput(session, region), ct);
        var summaryEvent = session.Append(new CompactionSummaryEvent
        {
            CompactionId = compactionId.Value,
            Summary = summarized.Summary,
            RawOutput = summarized.RawOutput,
            LlmStreamCall = true,
            ShadowedRange = new ShadowedRange(region.StartSeq, region.EndSeq),
            ShadowedSeqs = region.ShadowedSeqs,
            ShadowedTokenCount = shadowedTokenCount,
            Provider = summarized.Provider,
            Model = summarized.Model,
            MaxTokens = summarized.MaxTokens,
            Usage = summarized.Usage,
        });
        var checkpointContent = new List<ContentBlock> { new TextBlock($"{CheckpointPreamble}\n\n{SummaryOpenTag}") };
        checkpointContent.AddRange(summarized.Summary);
        checkpointContent.Add(new TextBlock(SummaryCloseTag));
        var checkpointEvent = session.Append(new UserMessageEvent
        {
            Message = Messages.CreateUserMessage(checkpointContent, new PluginSource { Plugin = "compact", CompactionId = compactionId.Value }),
            SurfaceOp = SurfaceOp.Replace(region.StartSeq, region.EndSeq),
            SourceEventSeqs = new[] { startEvent.Seq, summaryEvent.Seq }.Concat(region.ShadowedSeqs).ToArray(),
        });
        var endEvent = session.Append(new CompactionEndEvent { CompactionId = compactionId.Value, Turn = turn });

        return new CompactionResult(
            compactionId,
            startEvent.Seq,
            summaryEvent.Seq,
            checkpointEvent.Seq,
            endEvent.Seq,
            string.Concat(summarized.Summary.OfType<TextBlock>().Select(block => block.Text)),
            region,
            shadowedTokenCount);
    }

    /// <summary>Replay the shadowed region plus the compaction instruction through one llm/stream call.</summary>
    private async Task<SummaryResult> SummarizeWithLlm(SummarizationInput input, CancellationToken ct)
    {
        var llm = Ctx.Get<LlmRuntime>("llm")
            ?? throw new InvalidOperationException("compaction: the \"llm\" service is not mounted; the summarization call needs it");
        var messages = input.Messages
            .Concat(new[] { Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(CompactionInstruction) }, new PluginSource { Plugin = "hsh-compaction-basic" }) })
            .ToArray();
        var options = new GenerateOptions(
            input.Provider, input.Model, messages,
            System: input.System, Tools: input.Tools,
            MaxTokens: input.MaxTokens,
            CancellationToken: ct,
            SessionId: input.SessionId,
            Purpose: "compaction");
        var assembler = new BlockAssembler();
        await foreach (var chunk in llm.Stream(options, ct)) assembler.Push(chunk);
        var failure = assembler.Finish switch
        {
            Harness.Llm.Error error => error.Failure,
            Harness.Llm.Aborted aborted => aborted.Failure,
            _ => null,
        };
        if (failure is not null) throw new LlmError(failure.Message, failure.Code, failure.Status);
        var rawOutput = assembler.Blocks();
        var summary = rawOutput.OfType<TextBlock>().ToArray();
        if (summary.Length == 0) throw new InvalidOperationException("summarization produced no text summary content");
        return new SummaryResult(summary, rawOutput, assembler.Usage, input.Provider, input.Model, input.MaxTokens);
    }

    /// <summary>Reconstruct the summary call's cacheable prefix from the last routed request header and the shadowed region.</summary>
    private SummarizationInput BuildSummarizationInput(Harness.Session.Session session, SurfaceSelection region)
    {
        var header = session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header).LastOrDefault();
        var shadowed = region.ShadowedSeqs.ToHashSet();
        var messages = Surface.Fold(session.Events)
            .Where(node => shadowed.Contains(node.Seq))
            .Select(node => node.Message)
            .ToArray();
        return new SummarizationInput(
            header?.Config.Provider ?? string.Empty,
            header?.Config.Model ?? string.Empty,
            header?.System,
            header?.Tools,
            messages,
            session.Id.Value,
            _maxTokens);
    }

    /// <summary>
    /// The request-error recovery listener: on a provider-confirmed context overflow, compact the
    /// session (zero retain budget) and ask the loop to retry as a new request series. Retries are
    /// capped per agent and reset when a successful assistant message lands or the agent idles.
    /// </summary>
    private void RegisterOverflowListener()
    {
        _listeners.Add(Ctx.On("session/event",
            new Action<Harness.Session.Session, SessionEvent>((session, evt) =>
            {
                if (evt is AssistantMessageEvent) _overflowRetries.Remove(session.Id.Value);
            })));
        _listeners.Add(Ctx.On(LoopEvents.RequestError,
            new Func<RequestErrorProposal, Func<Task<RequestErrorAction?>>, Task<RequestErrorAction?>>(async (proposal, next) =>
            {
                if (proposal.Failure.Code != ContextWindowExceededCode) return await next();
                var key = proposal.Agent.Session.Id.Value;
                if (_overflowRetries.GetValueOrDefault(key) >= _maxOverflowRetries) return await next();
                var header = proposal.Agent.Session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header).LastOrDefault();
                if (header is null) return await next();
                var request = new CompactionRequest(
                    proposal.Agent.Session,
                    ContextWindow: 0,
                    Provider: header.Config.Provider,
                    Model: header.Config.Model,
                    MaxTokens: _maxTokens);
                var result = await CompactOverflowAsync(request, proposal.CancellationToken);
                if (result is null) return await next();
                _overflowRetries[key] = _overflowRetries.GetValueOrDefault(key) + 1;
                return CompactionDecision.Instance;
            })));
    }

    /// <summary>Sum the estimator's per-node prices over the shadowed span.</summary>
    private long ShadowedTokenCount(Harness.Session.Session session, SurfaceSelection region)
    {
        var priced = _estimator(session);
        long total = 0;
        for (var i = region.StartIndex; i <= region.EndIndex; i++) total += priced[i];
        return total;
    }

    /// <summary>Reject a durable unmatched compaction marker: the lock is held until one compaction/end.</summary>
    private static void AssertCompactionInactive(Harness.Session.Session session)
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
    private static long? OpenTurn(Harness.Session.Session session)
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
