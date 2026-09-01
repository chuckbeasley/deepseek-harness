using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Compaction;
using Harness.Llm;
using Harness.Session;

namespace Harness.Compaction.Tests;

public static class CompactionTests
{
    /// <summary>A deterministic fake summarizer: one fixed text block, no usage.</summary>
    private static Task<SummaryResult> FakeSummarizer(string text)
        => Task.FromResult(new SummaryResult(
            new ContentBlock[] { new TextBlock(text) },
            new ContentBlock[] { new TextBlock(text) },
            Usage: null,
            Provider: "local",
            Model: "basic",
            MaxTokens: 8192));

    /// <summary>Seed three complete turns with deterministic token prices (see per-test comments).</summary>
    private static global::Harness.Session.Session SeedThreeTurns(global::Harness.Session.Session session)
    {
        // Node prices (ceil(len/4)+4 per block, +4 role): "first"=10, "reply one"=11,
        // "second message"=12, "reply two"=11, "third"=10, "done"=9. Surface nodes are seqs
        // 1,2,5,6,9,10.
        TurnStart(session, 1);
        User(session, "first");
        Assistant(session, 1, 1, "reply one");
        TurnEnd(session, 1);
        TurnStart(session, 2);
        User(session, "second message");
        Assistant(session, 2, 1, "reply two");
        TurnEnd(session, 2);
        TurnStart(session, 3);
        User(session, "third");
        Assistant(session, 3, 1, "done");
        TurnEnd(session, 3);
        return session;
    }

    public static void BudgetedTrim_ShadowsEverythingAboveTheRetainedTail()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx, summarizer: (_, _) => FakeSummarizer("condensed"));
        var session = SeedThreeTurns(store.Create());

        // retainTokens=5 keeps only the tail node "done" (9 tokens); the budget walk stops at
        // the first node whose accumulated tail reaches the budget, then walks left only while
        // the cut is tool-unbalanced — no turn alignment (the TS selectCompactableRange).
        var result = provider.Compact(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 5));
        Assert.NotNull(result, "a three-turn session over budget must compact");
        Assert.Equal(new long[] { 1, 2, 5, 6, 9 }, result!.Region.ShadowedSeqs, "the head is everything above the retained tail node");
        Assert.Equal(1L, result.Region.StartSeq);
        Assert.Equal(9L, result.Region.EndSeq);
        Assert.Equal(54L, result.ShadowedTokenCount, "shadowed tokens are the node prices 10+11+12+11+10");

        // The retained tail starts at seq 10; the checkpoint replaces the shadowed range in place.
        var retained = SessionSurface.Nodes(session).Where(seq => !result.Region.ShadowedSeqs.Contains(seq)).ToList();
        Assert.Equal(new long[] { result.CheckpointSeq, 10 }, retained, "the checkpoint replaces the shadowed head, the tail node survives");

        // The transaction appended start -> summary -> checkpoint -> end in order.
        var markers = session.Events.OfType<SessionEvent>().Where(evt => evt.Type.StartsWith("compaction/", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, markers.Count, "one start, one summary, one end");
        var summary = session.Events.OfType<CompactionSummaryEvent>().Single();
        Assert.Equal(result.CompactionId.Value, summary.CompactionId, "the summary carries the transaction identity");
        Assert.Equal(new long[] { 1, 2, 5, 6, 9 }, summary.ShadowedSeqs);
        Assert.Equal(new ShadowedRange(1, 9), summary.ShadowedRange, "the shadowed range is the inclusive span");
        Assert.True(summary.LlmStreamCall, "the summary is the one local llm/stream call");
        Assert.Equal(new[] { "condensed" }, summary.Summary.OfType<TextBlock>().Select(block => block.Text).ToArray());
        Assert.Equal("local", summary.Provider);
        Assert.Equal("basic", summary.Model);
        var checkpoint = session.Events.OfType<UserMessageEvent>().Last();
        var checkpointSource = Assert.IsType<PluginSource>(checkpoint.Message.Source);
        Assert.Equal("compact", checkpointSource.Plugin);
        Assert.Equal(result.CompactionId.Value, checkpointSource.CompactionId);
        Assert.IsType<SurfaceOpReplace>(checkpoint.SurfaceOp, "the checkpoint shadows the range in place");
        var checkpointTexts = checkpoint.Message.Content.OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Equal(3, checkpointTexts.Length, "the checkpoint frames the summary with the preamble and close tag");
        Assert.True(checkpointTexts[0].StartsWith("This is an automatically generated checkpoint", StringComparison.Ordinal)
            && checkpointTexts[0].EndsWith("<compacted-summary>", StringComparison.Ordinal));
        Assert.Equal("condensed", checkpointTexts[1]);
        Assert.Equal("</compacted-summary>", checkpointTexts[2]);
        Assert.True(summary.Seq < checkpoint.Seq && checkpoint.Seq < markers.Last().Seq,
            "summary precedes the checkpoint which precedes compaction/end");
    }

    public static void ToolPairing_KeepsCallAndResultTogether()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx, summarizer: (_, _) => FakeSummarizer("condensed"));
        var session = store.Create();

        // Turn 1 completes a tool pair; turn 2 ends with an open tool call; turn 3 holds the result.
        // With retainTokens=15 the budget cut lands between the open tool call and its result
        // (the pairing rule pushes the cut left to the turn 1/2 boundary so the pair survives).
        TurnStart(session, 1);
        var call1 = new ToolCallId("call-1");
        User(session, "one");
        AssistantToolCall(session, 1, 1, call1, "read", "a.txt");
        ToolResult(session, call1, "content");
        Assistant(session, 1, 1, "done");
        TurnEnd(session, 1);
        TurnStart(session, 2);
        var call2 = new ToolCallId("call-2");
        User(session, "two");
        AssistantToolCall(session, 2, 1, call2, "read", "b.txt");
        TurnEnd(session, 2);
        TurnStart(session, 3);
        ToolResult(session, call2, "b content");
        Assistant(session, 3, 1, "fin");
        TurnEnd(session, 3);

        var result = provider.Compact(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 15));
        Assert.NotNull(result, "the open tool-call turn must not block compaction of the complete head");
        // The shadowed head is turn 1 only: the tool call "read b.txt" and its result both survive.
        var shadowed = result!.Region.ShadowedSeqs;
        Assert.False(shadowed.Contains(call2Seq(session, call2)), "the open tool call must stay with its result");
        Assert.False(shadowed.Contains(resultSeq(session, call2)), "the result must stay with its call");
        Assert.Equal(1L, result.Region.StartSeq);
        var firstRetained = SessionSurface.Nodes(session).First(seq => !shadowed.Contains(seq));
        Assert.True(ToolPairing.BalancedBefore(session, firstRetained),
            "the selected cut must be tool-pairing balanced");
    }

    public static void DeterministicOutput_ForIdenticalSessions()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx, summarizer: (_, _) => FakeSummarizer("condensed"));

        var first = SeedThreeTurns(store.Create());
        var second = SeedThreeTurns(store.Create());
        var request = new CompactionRequest(first, ContextWindow: 1000, RetainTokens: 5);
        var resultA = provider.Compact(request);
        var resultB = provider.Compact(request with { Session = second });

        Assert.NotNull(resultA);
        Assert.NotNull(resultB);
        Assert.Equal(resultA!.Region.ShadowedSeqs, resultB!.Region.ShadowedSeqs, "identical sessions select the same region");
        Assert.Equal(resultA.Summary, resultB.Summary, "the checkpoint text is deterministic");
        Assert.Equal(resultA.ShadowedTokenCount, resultB.ShadowedTokenCount);
    }

    public static void NoCompactableRange_ForEmptyOrFullyRetainedLogs()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx, summarizer: (_, _) => FakeSummarizer("condensed"));

        var empty = store.Create();
        Assert.Null(provider.Compact(new CompactionRequest(empty, ContextWindow: 1000, RetainTokens: 5)),
            "an empty log has no compactable range");

        // A retention budget larger than the whole surface leaves no head either.
        var three = SeedThreeTurns(store.Create());
        Assert.Null(provider.Compact(new CompactionRequest(three, ContextWindow: 1000, RetainTokens: 100)),
            "a budget larger than the surface retains everything");

        // A single open turn with a tiny budget compacts (no turn alignment): the recorded
        // context-overflow fixture shadows exactly the mid-turn user messages.
        var single = store.Create();
        TurnStart(single, 1);
        User(single, "only message");
        Assistant(single, 1, 1, "only reply");
        var result = provider.Compact(new CompactionRequest(single, ContextWindow: 1000, RetainTokens: 0));
        Assert.NotNull(result, "a single open turn is compactable below the budget");
        Assert.Equal(new long[] { 1 }, result!.Region.ShadowedSeqs, "only the user message is shadowed");
    }

    public static void BudgetValidation_FailsLoud()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx);
        var session = SeedThreeTurns(store.Create());

        var zeroWindow = Assert.Throws<TargetPressureConfigError>(
            () => provider.Resolve(new CompactionRequest(session, ContextWindow: 0, RetainTokens: 5)));
        Assert.True(zeroWindow.TargetKey == "local/basic", "the target key names the route");

        Assert.Throws<TargetPressureConfigError>(
            () => provider.Resolve(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 900)),
            "retainTokens must be below the threshold (800)");

        Assert.Throws<CompactionConfigError>(
            () => provider.Resolve(new CompactionRequest(session, ContextWindow: 1000, RetainRatio: 0.9)),
            "retainRatio must be below thresholdRatio");

        Assert.Throws<CompactionConfigError>(
            () => provider.Resolve(new CompactionRequest(session, ContextWindow: 1000, RetainRatio: 0.16, RetainTokens: 5)),
            "retainRatio and retainTokens are mutually exclusive");

        Assert.Throws<CompactionConfigError>(
            () => provider.Resolve(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: -1)),
            "retainTokens must be non-negative");

        Assert.Throws<CompactionConfigError>(
            () => provider.Resolve(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 5, Provider: "")),
            "provider must be non-empty");
    }

    public static void BusyLock_RejectsConcurrentCompaction()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx, summarizer: (_, _) => FakeSummarizer("condensed"));
        var session = SeedThreeTurns(store.Create());

        // A durable compaction/start without its compaction/end holds the lock.
        session.Append(new CompactionStartEvent { CompactionId = "stale-1", Turn = 3 });
        var busy = Assert.Throws<ManualCompactionError>(
            () => provider.Compact(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 5)));
        Assert.Equal(ManualCompactionErrorCode.Busy, busy.Code);
    }

    public static void CompactionEvents_RoundTripTheJsonl()
    {
        using var ctx = new Context();
        _ = new BasicCompactionProvider(ctx); // registers the compaction/* discriminators

        var evt = new CompactionSummaryEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            CompactionId = "c-1",
            Summary = new ContentBlock[] { new TextBlock("condensed") },
            RawOutput = new ContentBlock[] { new TextBlock("condensed") },
            LlmStreamCall = true,
            ShadowedRange = new ShadowedRange(1, 2),
            ShadowedSeqs = new long[] { 1, 2 },
            ShadowedTokenCount = 5,
            Provider = "local",
            Model = "basic",
            MaxTokens = 32,
            Usage = new TokenUsage(20, 4),
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var json = JsonSerializer.Serialize<SessionEvent>(evt, options);
        var back = Assert.IsType<CompactionSummaryEvent>(JsonSerializer.Deserialize<SessionEvent>(json, options));

        Assert.Equal(evt.Type, back.Type);
        Assert.Equal(evt.Id, back.Id);
        Assert.Equal(evt.Seq, back.Seq);
        Assert.Equal(evt.CompactionId, back.CompactionId);
        Assert.Equal(evt.Summary, back.Summary);
        Assert.Equal(evt.RawOutput, back.RawOutput);
        Assert.Equal(evt.LlmStreamCall, back.LlmStreamCall);
        Assert.Equal(evt.ShadowedRange, back.ShadowedRange);
        Assert.Equal(evt.ShadowedSeqs, back.ShadowedSeqs);
        Assert.Equal(evt.ShadowedTokenCount, back.ShadowedTokenCount);
        Assert.Equal(evt.Provider, back.Provider);
        Assert.Equal(evt.Model, back.Model);
        Assert.Equal(evt.MaxTokens, back.MaxTokens);
        Assert.Equal(evt.Usage, back.Usage);
    }

    public static void RegistersAsTheCompactionService()
    {
        using var ctx = new Context();
        var provider = new BasicCompactionProvider(ctx);

        Assert.Same(provider, ctx.Get<ICompactionService>("compaction"));
        Assert.Same(provider, BasicCompactionProvider.Require(ctx));
    }

    // --- seeding helpers ---

    private static void TurnStart(global::Harness.Session.Session session, long turn) => session.Append(new TurnStartEvent { Turn = turn });

    private static void TurnEnd(global::Harness.Session.Session session, long turn)
        => session.Append(new TurnEndEvent { Turn = turn, Reason = new CompletedReason() });

    private static void User(global::Harness.Session.Session session, string text)
        => session.Append(new UserMessageEvent
        {
            Message = Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void Assistant(global::Harness.Session.Session session, long turn, long step, string text)
        => session.Append(new AssistantMessageEvent
        {
            Turn = turn,
            Step = step,
            Message = Messages.CreateAssistantMessage("local", "test", new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void AssistantToolCall(global::Harness.Session.Session session, long turn, long step, ToolCallId callId, string name, string args)
        => session.Append(new AssistantMessageEvent
        {
            Turn = turn,
            Step = step,
            Message = Messages.CreateAssistantMessage("local", "test",
                new ContentBlock[] { new ToolCallBlock(callId, name, args) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void ToolResult(global::Harness.Session.Session session, ToolCallId callId, string text)
        => session.Append(new ToolResultEvent
        {
            Turn = 0,
            Step = 0,
            Message = ToolResultMessage.Create(callId, new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    /// <summary>The seq of the assistant message carrying the named tool call.</summary>
    private static long call2Seq(global::Harness.Session.Session session, ToolCallId callId)
        => session.Events.OfType<AssistantMessageEvent>()
            .First(evt => evt.Message.Content.OfType<ToolCallBlock>().Any(block => block.Id == callId)).Seq;

    /// <summary>The seq of the tool result answering the named call.</summary>
    private static long resultSeq(global::Harness.Session.Session session, ToolCallId callId)
        => session.Events.OfType<ToolResultEvent>().First(evt => evt.Message.Result.ToolCallId == callId).Seq;
}
