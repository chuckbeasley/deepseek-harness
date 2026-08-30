using System.Text.Json;
using Cordis.Core;
using Dsh.Compaction;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Compaction.Tests;

public static class CompactionTests
{
    /// <summary>Seed three complete turns with deterministic token prices (see per-test comments).</summary>
    private static Dsh.Session.Session SeedThreeTurns(Dsh.Session.Session session)
    {
        // Tokens (ceil(chars/4), min 1): "first"=2, "reply one"=3, "second message"=4,
        // "reply two"=3, "third"=2, "done"=1. Surface nodes are seqs 1,2,5,6,9,10.
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

    public static void BudgetedTrim_KeepsTurnBoundaries()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx);
        var session = SeedThreeTurns(store.Create());

        // retainTokens=5 keeps the turn-3 tail (2+1=3 tokens) and part of turn 2; the cut walks
        // left to the turn 1/2 boundary so the compacted head is whole turns only.
        var result = provider.Compact(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 5));
        Assert.NotNull(result, "a three-turn session over budget must compact");
        Assert.Equal(new long[] { 1, 2 }, result!.Region.ShadowedSeqs, "the head is exactly turn 1");
        Assert.Equal(1L, result.Region.StartSeq);
        Assert.Equal(2L, result.Region.EndSeq);
        Assert.Equal(5L, result.ShadowedTokenCount, "shadowed tokens are the turn-1 prices 2+3");

        // The retained tail starts at seq 5, the first surface node of turn 2.
        var retained = SessionSurface.Nodes(session).Where(seq => !result.Region.ShadowedSeqs.Contains(seq)).ToList();
        Assert.Equal(new long[] { 5, 6, 9, 10 }, retained.Take(4).ToList(), "turns 2 and 3 remain verbatim");
        Assert.Equal(1, retained.Count - 4, "the only extra retained node is the appended checkpoint");

        // The transaction appended start -> summary -> checkpoint -> end in order.
        var markers = session.Events.OfType<SessionEvent>().Where(evt => evt.Type.StartsWith("compaction/")).ToList();
        Assert.Equal(3, markers.Count, "one start, one summary, one end");
        var summary = session.Events.OfType<CompactionSummaryEvent>().Single();
        Assert.Equal(result.CompactionId.Value, summary.CompactionId, "the summary carries the transaction identity");
        Assert.Equal(new long[] { 1, 2 }, summary.ShadowedSeqs);
        Assert.Equal("local", summary.Provider);
        Assert.Equal("basic", summary.Model);
        var checkpoint = session.Events.OfType<UserMessageEvent>().Last();
        Assert.Equal("compaction", ((PluginSource)checkpoint.Message.Source).Plugin);
        Assert.True(checkpoint.Message.Content.OfType<TextBlock>().Single().Text.Contains("Compacted 2 surface nodes (seqs 1-2"),
            "the checkpoint text names the shadowed span");
        Assert.True(summary.Seq < checkpoint.Seq && checkpoint.Seq < markers.Last().Seq,
            "summary precedes the checkpoint which precedes compaction/end");
    }

    public static void ToolPairing_KeepsCallAndResultTogether()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx);
        var session = store.Create();

        // Turn 1 completes a tool pair; turn 2 ends with an open tool call; turn 3 holds the result.
        // Node prices: 1,3,2,1,1,3,3,1. With retainTokens=4 the budget cut lands at the turn 2/3
        // boundary (before the result), which is turn-aligned but tool-unbalanced; the pairing rule
        // pushes the cut left to the turn 1/2 boundary.
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

        var result = provider.Compact(new CompactionRequest(session, ContextWindow: 1000, RetainTokens: 4));
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
        var provider = new BasicCompactionProvider(ctx);

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

    public static void NoCompactableRange_ForEmptyOrSingleTurnLogs()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var provider = new BasicCompactionProvider(ctx);

        var empty = store.Create();
        Assert.Null(provider.Compact(new CompactionRequest(empty, ContextWindow: 1000, RetainTokens: 5)),
            "an empty log has no compactable range");

        // A single open turn can never be cut at a turn boundary.
        var single = store.Create();
        TurnStart(single, 1);
        User(single, "only message");
        Assistant(single, 1, 1, "only reply");
        Assert.Null(provider.Compact(new CompactionRequest(single, ContextWindow: 1000, RetainTokens: 5)),
            "a single open turn cannot be split");

        // One complete turn: the only boundary is before the first node, which is not selectable.
        var oneTurn = store.Create();
        TurnStart(oneTurn, 1);
        User(oneTurn, "first");
        Assistant(oneTurn, 1, 1, "done");
        TurnEnd(oneTurn, 1);
        Assert.Null(provider.Compact(new CompactionRequest(oneTurn, ContextWindow: 1000, RetainTokens: 5)),
            "one complete turn has no interior turn boundary");

        // A retention budget larger than the whole surface leaves no head either.
        var three = SeedThreeTurns(store.Create());
        Assert.Null(provider.Compact(new CompactionRequest(three, ContextWindow: 1000, RetainTokens: 100)),
            "a budget larger than the surface retains everything");
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
        var provider = new BasicCompactionProvider(ctx);
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
            Summary = "summary text",
            RangeStart = 1,
            RangeEnd = 2,
            ShadowedSeqs = new long[] { 1, 2 },
            ShadowedTokenCount = 5,
            Provider = "local",
            Model = "basic",
            MaxTokens = 8192,
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var json = JsonSerializer.Serialize<SessionEvent>(evt, options);
        var back = Assert.IsType<CompactionSummaryEvent>(JsonSerializer.Deserialize<SessionEvent>(json, options));

        Assert.Equal(evt.Type, back.Type);
        Assert.Equal(evt.Id, back.Id);
        Assert.Equal(evt.Seq, back.Seq);
        Assert.Equal(evt.CompactionId, back.CompactionId);
        Assert.Equal(evt.Summary, back.Summary);
        Assert.Equal(evt.RangeStart, back.RangeStart);
        Assert.Equal(evt.RangeEnd, back.RangeEnd);
        Assert.Equal(evt.ShadowedSeqs, back.ShadowedSeqs);
        Assert.Equal(evt.ShadowedTokenCount, back.ShadowedTokenCount);
        Assert.Equal(evt.Provider, back.Provider);
        Assert.Equal(evt.Model, back.Model);
        Assert.Equal(evt.MaxTokens, back.MaxTokens);
    }

    public static void RegistersAsTheCompactionService()
    {
        using var ctx = new Context();
        var provider = new BasicCompactionProvider(ctx);

        Assert.Same(provider, ctx.Get<ICompactionService>("compaction"));
        Assert.Same(provider, BasicCompactionProvider.Require(ctx));
    }

    // --- seeding helpers ---

    private static void TurnStart(Dsh.Session.Session session, long turn) => session.Append(new TurnStartEvent { Turn = turn });

    private static void TurnEnd(Dsh.Session.Session session, long turn)
        => session.Append(new TurnEndEvent { Turn = turn, Reason = new CompletedReason() });

    private static void User(Dsh.Session.Session session, string text)
        => session.Append(new UserMessageEvent
        {
            Message = Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void Assistant(Dsh.Session.Session session, long turn, long step, string text)
        => session.Append(new AssistantMessageEvent
        {
            Turn = turn,
            Step = step,
            Message = Messages.CreateAssistantMessage("local", "test", new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void AssistantToolCall(Dsh.Session.Session session, long turn, long step, ToolCallId callId, string name, string args)
        => session.Append(new AssistantMessageEvent
        {
            Turn = turn,
            Step = step,
            Message = Messages.CreateAssistantMessage("local", "test",
                new ContentBlock[] { new ToolCallBlock(callId, name, args) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void ToolResult(Dsh.Session.Session session, ToolCallId callId, string text)
        => session.Append(new ToolResultEvent
        {
            Turn = 0,
            Step = 0,
            Message = ToolResultMessage.Create(callId, new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    /// <summary>The seq of the assistant message carrying the named tool call.</summary>
    private static long call2Seq(Dsh.Session.Session session, ToolCallId callId)
        => session.Events.OfType<AssistantMessageEvent>()
            .First(evt => evt.Message.Content.OfType<ToolCallBlock>().Any(block => block.Id == callId)).Seq;

    /// <summary>The seq of the tool result answering the named call.</summary>
    private static long resultSeq(Dsh.Session.Session session, ToolCallId callId)
        => session.Events.OfType<ToolResultEvent>().First(evt => evt.Message.Result.ToolCallId == callId).Seq;
}
