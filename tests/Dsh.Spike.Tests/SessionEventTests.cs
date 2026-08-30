using System.Text.Json;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Spike.Tests;

public static class SessionEventTests
{
    private const long T = 1_700_000_000_000;

    /// <summary>One constructed instance of every spike event type (spike-design.md section 3.1).</summary>
    private static SessionEvent[] FixtureEvents() => new SessionEvent[]
    {
        new TurnStartEvent { Id = "evt-0", Seq = 0, TimeMs = T, Turn = 1 },
        new TurnEndEvent { Id = "evt-1", Seq = 1, TimeMs = T, Turn = 1, Reason = new CompletedReason() },
        new StepStartEvent { Id = "evt-2", Seq = 2, TimeMs = T, Turn = 1, Step = 1 },
        new StepEndEvent { Id = "evt-3", Seq = 3, TimeMs = T, Turn = 1, Step = 1 },
        new UserMessageEvent
        {
            Id = "evt-4", Seq = 4, TimeMs = T,
            Message = new UserMessage
            {
                Id = new MessageId("msg-user-1"),
                Content = new ContentBlock[] { new TextBlock("Record your plan.") },
                Source = new UserSource(),
            },
            SurfaceOp = SurfaceOp.Append,
        },
        new AssistantChunkEvent { Id = "evt-5", Seq = 5, TimeMs = T, Turn = 1, Step = 1, Chunk = new TextDelta(0, "he") },
        new AssistantMessageEvent
        {
            Id = "evt-6", Seq = 6, TimeMs = T, Turn = 1, Step = 1,
            Message = new AssistantMessage
            {
                Id = new MessageId("msg-assistant-1"),
                Content = new ContentBlock[] { new TextBlock("hello") },
                Source = new ModelSource { Provider = "mock", Model = "mock-todo" },
            },
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { 5 },
        },
        new ToolCallEvent
        {
            Id = "evt-7", Seq = 7, TimeMs = T, Turn = 1, Step = 1,
            CallId = new ToolCallId("call-1"), Name = "todo_write", Arguments = "{}",
        },
        new ToolResultEvent
        {
            Id = "evt-8", Seq = 8, TimeMs = T, Turn = 1, Step = 1,
            Message = new ToolResultMessage
            {
                Id = new MessageId("msg-tool-1"),
                Content = new ContentBlock[]
                {
                    new ToolResultBlock(new ToolCallId("call-1"), new ContentBlock[] { new TextBlock("ok") }),
                },
                Source = new ToolSource { CallId = new ToolCallId("call-1") },
            },
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { 7 },
        },
        new RequestHeaderEvent
        {
            Id = "evt-9", Seq = 9, TimeMs = T,
            Header = new EpochHeader
            {
                Config = new LlmCallConfig("mock", "mock-todo"),
                System = "You are the Dsh port spike assistant.",
            },
            Reason = RequestHeaderReason.Initial,
        },
        new RequestContextEvent { Id = "evt-10", Seq = 10, TimeMs = T, Provider = "mock", Model = "mock-todo" },
    };

    public static void AllEventTypes_RoundTrip_ThroughSystemTextJson()
    {
        foreach (var evt in FixtureEvents())
        {
            var json = JsonSerializer.Serialize<SessionEvent>(evt);
            var back = JsonSerializer.Deserialize<SessionEvent>(json);
            Assert.NotNull(back);
            // Deep structural comparison (record equality is reference-based for collection props).
            Assert.Equal(evt, back);
            Assert.Equal(evt.Type, back!.Type);
        }
    }

    public static void EventEnvelope_RecordsSeqAndTime()
    {
        var evt = (TurnStartEvent)FixtureEvents()[0];
        Assert.Equal("evt-0", evt.Id);
        Assert.Equal(0L, evt.Seq);
        Assert.Equal(T, evt.TimeMs);
        Assert.Equal("turn/start", evt.Type);
    }
}
