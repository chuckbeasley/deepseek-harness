using Harness.Llm;
using Harness.Session;

namespace Harness.Spike.Tests;

public static class SurfaceTests
{
    public static void UserMessageEvent_DerivesToItsMessage()
    {
        var message = new UserMessage
        {
            Id = new MessageId("m1"),
            Content = new ContentBlock[] { new TextBlock("hi") },
            Source = new UserSource(),
        };
        var evt = new UserMessageEvent { Id = "e1", Seq = 0, TimeMs = 1, Message = message, SurfaceOp = SurfaceOp.Append };
        Assert.Same(message, Surface.DeriveEventMessage(evt));
    }

    public static void EmptyAssistantMessage_DerivesToNull()
    {
        var evt = new AssistantMessageEvent
        {
            Id = "e2", Seq = 0, TimeMs = 1, Turn = 1, Step = 1,
            Message = new AssistantMessage
            {
                Id = new MessageId("m2"),
                Content = Array.Empty<ContentBlock>(),
                Source = new ModelSource { Provider = "mock", Model = "mock-todo" },
            },
            SurfaceOp = SurfaceOp.Append,
        };
        Assert.Null(Surface.DeriveEventMessage(evt));
    }

    public static void ToolResultEvent_DerivesToItsMessage()
    {
        var message = new ToolResultMessage
        {
            Id = new MessageId("m3"),
            Content = new ContentBlock[]
            {
                new ToolResultBlock(new ToolCallId("call-1"), new ContentBlock[] { new TextBlock("ok") }),
            },
            Source = new ToolSource { CallId = new ToolCallId("call-1") },
        };
        var evt = new ToolResultEvent
        {
            Id = "e3", Seq = 0, TimeMs = 1, Turn = 1, Step = 1,
            Message = message, SurfaceOp = SurfaceOp.Append,
        };
        Assert.Same(message, Surface.DeriveEventMessage(evt));
    }

    public static void NonSurfaceEvent_DerivesToNull()
    {
        var evt = new TurnStartEvent { Id = "e4", Seq = 0, TimeMs = 1, Turn = 1 };
        Assert.Null(Surface.DeriveEventMessage(evt));
    }

    public static void IsSurfaceEligibleType_MatchesTheThreeMessageEvents()
    {
        Assert.True(Surface.IsSurfaceEligibleType("user/message"));
        Assert.True(Surface.IsSurfaceEligibleType("assistant/message"));
        Assert.True(Surface.IsSurfaceEligibleType("tool/result"));
        Assert.False(Surface.IsSurfaceEligibleType("turn/start"));
        Assert.False(Surface.IsSurfaceEligibleType("assistant/chunk"));
    }
}

