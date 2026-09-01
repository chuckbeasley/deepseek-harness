using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Feedback.Tests;

public static class FeedbackTests
{
    private static readonly MessageId MessageA = new("msg-1");
    private static readonly MessageId MessageB = new("msg-2");

    public static void EmptyLog_YieldsAnEmptyState()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionFeedbackService(ctx);
        var session = sessions.Create();

        Assert.Empty(service.Current(session).Items, "a log with no feedback/write folds to an empty state");
    }

    public static void FeedbackEvents_FoldLastWriteWinsPerMessageInCreationOrder()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionFeedbackService(ctx);
        var session = sessions.Create();

        session.Append(new FeedbackEvent { MessageId = MessageA, Item = new FeedbackItem(MessageA, MessageFeedbackRating.Positive, "clear answer", 1, 1) });
        session.Append(new FeedbackEvent { MessageId = MessageB, Item = new FeedbackItem(MessageB, MessageFeedbackRating.Negative, null, 2, 2) });
        session.Append(new FeedbackEvent { MessageId = MessageA, Item = new FeedbackItem(MessageA, MessageFeedbackRating.Negative, "too terse", 1, 3) });

        var items = service.Current(session).Items;
        Assert.Equal(2, items.Count, "one item per message, in first-creation order");
        Assert.Equal(MessageA, items[0].MessageId);
        Assert.Equal(MessageFeedbackRating.Negative, items[0].Rating, "the latest write for a message wins");
        Assert.Equal("too terse", items[0].Note);
        Assert.Equal(1L, items[0].CreatedAt, "a rewrite retains the creation time");
        Assert.Equal(3L, items[0].UpdatedAt);
        Assert.Equal(MessageB, items[1].MessageId);
        Assert.Equal(MessageFeedbackRating.Negative, items[1].Rating);
    }

    public static void StateUpdatesLiveOnSessionEvent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionFeedbackService(ctx);
        var session = sessions.Create();

        session.Append(new TurnStartEvent { Turn = 1 });
        Assert.Empty(service.Current(session).Items, "non-feedback events must not change the state");

        service.Put(session, MessageA, MessageFeedbackRating.Positive, "clear answer");
        var live = service.Current(session).Items;
        Assert.Equal(1, live.Count);
        Assert.Equal(MessageFeedbackRating.Positive, live[0].Rating);
    }

    public static void FeedbackEvent_RoundTripsTheJsonl()
    {
        using var ctx = new Context();
        _ = new SessionFeedbackService(ctx); // registers feedback/write into the session event-type registry

        var put = new FeedbackEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            MessageId = MessageA,
            Item = new FeedbackItem(MessageA, MessageFeedbackRating.Positive, "clear answer", 1, 1),
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var putBack = Assert.IsType<FeedbackEvent>(
            JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize<SessionEvent>(put, options), options));
        Assert.Equal("feedback/write", putBack.Type);
        Assert.Equal(MessageA, putBack.MessageId);
        Assert.Equal(put.Item, putBack.Item);

        var delete = new FeedbackEvent
        {
            Id = "evt-1",
            Seq = 1,
            TimeMs = 2,
            MessageId = MessageA,
            Item = null,
        };
        var deleteBack = Assert.IsType<FeedbackEvent>(
            JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize<SessionEvent>(delete, options), options));
        Assert.Null(deleteBack.Item, "a delete event round-trips without an item");
    }

    public static void MessageFeedbackTool_ExecutesThroughToolRuntime_AndAppendsTheDurableEvent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var tools = new ToolRuntime(ctx);
        var service = new SessionFeedbackService(ctx);
        tools.Register(FeedbackTools.Definition(service));
        var session = sessions.Create();

        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"message_id\":\"msg-1\",\"rating\":\"positive\",\"note\":\"clear answer\"}")
            !);
        var input = new ToolExecutionInput(new ToolCallId("call-1"), "message_feedback", args, CancellationToken.None) { Session = session };
        var result = tools.ExecuteAsync(input, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.IsError, "message_feedback must succeed through the tool runtime");
        var success = Assert.IsType<ToolExecutionSuccess>(result);
        Assert.Equal("msg-1", success.Value.GetProperty("messageId").GetString());
        Assert.Equal("positive", success.Value.GetProperty("rating").GetString());
        Assert.Equal("clear answer", success.Value.GetProperty("note").GetString());
        var text = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.Equal("Feedback recorded for message msg-1: positive.", text.Text);

        var committed = Assert.Single(session.Events.OfType<FeedbackEvent>());
        Assert.Equal(MessageA, committed.MessageId);
        Assert.Equal(MessageFeedbackRating.Positive, committed.Item!.Rating);
        Assert.Equal("clear answer", committed.Item.Note);
        // The append published through session/event, so the folded state followed it.
        Assert.Equal(committed.Item, service.Current(session).Items[0]);
    }

    public static void Put_RejectsBlankAndOversizedNotes()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionFeedbackService(ctx, maxNoteBytes: 16);
        var session = sessions.Create();

        var blank = Assert.Throws<FeedbackError>(() => service.Put(session, MessageA, MessageFeedbackRating.Positive, "   "));
        Assert.Equal(FeedbackErrorCode.NoteBlank, blank.Code);
        var large = Assert.Throws<FeedbackError>(() => service.Put(session, MessageA, MessageFeedbackRating.Positive, "this note is far too long"));
        Assert.Equal(FeedbackErrorCode.NoteTooLarge, large.Code);
        Assert.Empty(service.Current(session).Items, "a rejected note must not append an event");
        Assert.Empty(session.Events.OfType<FeedbackEvent>(), "a rejected note must not append an event");
    }

    public static void Delete_RemovesAnItem_AndAbsenceIsIdempotent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionFeedbackService(ctx);
        var session = sessions.Create();

        service.Put(session, MessageA, MessageFeedbackRating.Positive);
        Assert.True(service.Delete(session, MessageA), "deleting an existing item reports true");
        Assert.Empty(service.Current(session).Items);
        Assert.False(service.Delete(session, MessageA), "deleting an absent item reports false and appends nothing");
        Assert.Equal(2, session.Events.OfType<FeedbackEvent>().Count(), "only the put and the first delete append");
    }

    public static void RegistersAsTheFeedbackService()
    {
        using var ctx = new Context();
        var service = new SessionFeedbackService(ctx);

        Assert.Same(service, ctx.Get<IFeedbackService>("feedback"));
        Assert.Same(service, SessionFeedbackService.Require(ctx));
    }
}
