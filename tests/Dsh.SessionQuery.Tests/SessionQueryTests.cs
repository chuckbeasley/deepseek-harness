using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.SessionQuery;

namespace Dsh.SessionQuery.Tests;

public static class SessionQueryTests
{
    /// <summary>Seed a two-turn session with a tool round trip.</summary>
    private static Dsh.Session.Session Seed(SessionStore store)
    {
        var session = store.Create();
        session.Append(new TurnStartEvent { Turn = 1 });
        User(session, "first question");
        Assistant(session, 1, 1, "first answer");
        session.Append(new ToolCallEvent { Turn = 1, Step = 1, CallId = new ToolCallId("call-1"), Name = "read", Arguments = "a.txt" });
        ToolResult(session, new ToolCallId("call-1"), "file content");
        session.Append(new TurnEndEvent { Turn = 1, Reason = new CompletedReason() });
        session.Append(new TurnStartEvent { Turn = 2 });
        User(session, "second question");
        Assistant(session, 2, 1, "second answer");
        session.Append(new TurnEndEvent { Turn = 2, Reason = new CompletedReason() });
        return session;
    }

    public static void EventTypeFilters_SelectOnlyMatchingEvents()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        var results = query.EventsByType(session, "tool/result");
        Assert.Equal(1, results.Count);
        Assert.Equal(4L, results[0].Seq, "the tool result is the only tool/result event");

        var users = query.EventsByType(session, "user/message");
        Assert.Equal(new long[] { 1, 7 }, users.Select(evt => evt.Seq).ToList(), "user messages in log order");

        Assert.Empty(query.EventsByType(session, "unknown/type"), "an unknown type matches nothing");
    }

    public static void MessageFold_DerivesTheSurface()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        var messages = query.Messages(session);
        Assert.Equal(5, messages.Count, "two users, two assistants, one tool result");
        Assert.Equal(new[] { "user", "assistant", "user", "user", "assistant" },
            messages.Select(message => message.Role).ToList(), "the surface fold preserves role order");
        Assert.True(messages[0].Content.OfType<TextBlock>().Single().Text == "first question");
        Assert.True(messages[2] is ToolResultMessage, "the tool result derives a tool-result message");
    }

    public static void TurnEnumeration_FoldsOpenAndClosedTurns()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        var turns = query.Turns(session);
        Assert.Equal(2, turns.Count);
        Assert.Equal(new TurnRecord(1, 0, 5), turns[0], "turn 1 opens at seq 0 and closes at seq 5");
        Assert.Equal(new TurnRecord(2, 6, 9), turns[1], "turn 2 opens at seq 6 and closes at seq 9");

        // An open turn folds with a null end seq.
        var open = store.Create();
        open.Append(new TurnStartEvent { Turn = 1 });
        User(open, "still running");
        var openTurns = query.Turns(open);
        Assert.Equal(1, openTurns.Count);
        Assert.Null(openTurns[0].EndSeq, "an open turn has no end seq");
    }

    public static void Filters_AndWithinAClause_AndTextMatching()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        var toolResults = query.FilterEvents(session, new SessionEventFilter[] { new TypeFilter(new[] { "tool/result" }) });
        Assert.Equal(1, toolResults.Count);
        Assert.Equal("current", toolResults[0].Surface, "message-producing events are current surface");

        var text = query.FilterEvents(session, new SessionEventFilter[] { new TextFilter("FILE CONTENT") });
        Assert.Equal(1, text.Count, "the text scan is case-insensitive");

        var flexible = query.FilterEvents(session, new SessionEventFilter[] { new TextFilter("first\nquestion") });
        Assert.Equal(1, flexible.Count, "the text scan is whitespace-flexible");

        var seqRange = query.FilterEvents(session, new SessionEventFilter[] { new SeqRangeFilter(new SessionResultRange(From: 2, To: 4)) });
        Assert.Equal(new long[] { 2, 3, 4 }, seqRange.Select(doc => doc.Seq).ToList(), "the seq range is inclusive");

        var logOnly = query.FilterEvents(session, new SessionEventFilter[] { new SurfaceFilter(new[] { SessionEventSurfaces.LogOnly }) });
        Assert.True(logOnly.Count > 0 && logOnly.All(doc => doc.Type != "user/message"),
            "log-only filters keep structural events out of the surface set");

        // Clauses are ANDed; the type clause and the seq clause both apply.
        var anded = query.FilterEvents(session, new SessionEventFilter[]
        {
            new TypeFilter(new[] { "user/message" }),
            new SeqRangeFilter(new SessionResultRange(From: 7)),
        });
        Assert.Equal(new long[] { 7 }, anded.Select(doc => doc.Seq).ToList(), "filters AND across clauses");
    }

    public static void InvalidFilters_FailLoud()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        var emptyText = Assert.Throws<SessionQueryError>(
            () => query.FilterEvents(session, new SessionEventFilter[] { new TextFilter("   ") }));
        Assert.Equal(SessionQueryErrorCodes.InvalidFilter, emptyText.Code);

        Assert.Throws<SessionQueryError>(
            () => query.FilterEvents(session, new SessionEventFilter[] { new SeqRangeFilter(new SessionResultRange(From: 5, To: 2)) }),
            "a reversed range is invalid");

        Assert.Throws<SessionQueryError>(
            () => query.FilterEvents(session, new SessionEventFilter[] { new SurfaceFilter(new[] { "bogus" }) }),
            "an unknown surface value is invalid");

        Assert.Throws<SessionQueryError>(
            () => query.FilterEvents(session, new SessionEventFilter[] { new TypeFilter(Array.Empty<string>()) }),
            "an empty type clause is invalid");
    }

    public static void FoldHelper_AccumulatesOverEvents()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        var userCount = query.Fold(session, 0, (count, evt) => evt is UserMessageEvent ? count + 1 : count);
        Assert.Equal(2, userCount, "the fold counts user messages");

        var seqSum = query.Fold(session, 0L, (sum, evt) => sum + evt.Seq);
        Assert.Equal(45L, seqSum, "the fold sums every seq 0..9");
    }

    public static void ExtractEventText_SemanticText()
    {
        using var ctx = new Context();
        var store = new SessionStore(ctx);
        var query = new LogSessionQueryProvider(ctx);
        var session = Seed(store);

        Assert.Equal("first question", query.ExtractEventText(session.Events[1]), "user text is searchable");
        Assert.Equal("first answer", query.ExtractEventText(session.Events[2]), "assistant text is searchable");
        Assert.Equal("read\na.txt", query.ExtractEventText(session.Events[3]), "tool calls join name and arguments");
        Assert.Equal("file content", query.ExtractEventText(session.Events[4]), "tool results are searchable");
        Assert.Equal(string.Empty, query.ExtractEventText(session.Events[0]), "turn/start contributes no text");

        var failed = store.Create();
        failed.Append(new TurnEndEvent { Turn = 1, Reason = new ErrorReason(new LlmFailure("boom", "E_BOOM")) });
        Assert.Equal("error\nboom", query.ExtractEventText(failed.Events[0]), "a failing turn end is searchable");
    }

    public static void RegistersAsTheSessionQueryService()
    {
        using var ctx = new Context();
        var provider = new LogSessionQueryProvider(ctx);

        Assert.Same(provider, ctx.Get<ISessionQueryService>("sessionQuery"));
        Assert.Same(provider, LogSessionQueryProvider.Require(ctx));
    }

    // --- seeding helpers ---

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

    private static void ToolResult(Dsh.Session.Session session, ToolCallId callId, string text)
        => session.Append(new ToolResultEvent
        {
            Turn = 1,
            Step = 1,
            Message = ToolResultMessage.Create(callId, new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });
}
