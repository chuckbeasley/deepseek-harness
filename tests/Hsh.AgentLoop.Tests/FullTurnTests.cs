namespace Harness.AgentLoop.Tests;

/// <summary>One headless task end-to-end: tool call, then text, through the real agent loop.</summary>
public static class FullTurnTests
{
    public static async Task RunAsync(Harness h)
    {
        var (_, agent, loop) = h.CreateAgent("session-fullturn");
        var turnStoppingCount = 0;
        using var turnStopping = h.Ctx.On("agent/turn-stopping", new Action<TurnStoppingProposal>(_ => turnStoppingCount++));
        using var sessionStart = h.Ctx.On("agent/session-start", new Action<SessionStartPayload>(payload =>
        {
            Assert.Equal("startup", payload.Source, "create must announce session-start with the startup source");
        }));

        var user = Harness.Prompt("Record your plan for the .NET port as todos.");
        loop.Send(user, InboxTarget.NextTurn, wakeup: true);
        await loop.WhenIdleAsync();

        var types = agent.Session.Events.Select(evt => evt.Type).ToArray();
        Assert.Sequence(new[] { "agent/inbox/spliced", "turn/start", "agent/inbox/spliced", "step/start", "user/message" },
            types.Take(5).ToArray(), "the durable inbox splice opens the log, then the turn, the consume splice, the step, and the claimed message");
        Assert.Equal(1, types.Count(type => type == "request/header"), "request/header must be logged once (initial)");
        Assert.Equal(1, types.Count(type => type == "request/context"), "request/context must be logged once");
        Assert.Equal(2, types.Count(type => type == "step/start"), "the turn must run two steps");
        Assert.Equal(1, types.Count(type => type == "tool/call"), "one tool call must be logged");
        Assert.Equal(1, types.Count(type => type == "tool/result"), "one tool result must be logged");
        Assert.Equal(1, types.Count(type => type == "todo/write"), "the todo tool must append its durable event");
        Assert.Equal(1, types.Count(type => type == "turn/end"), "one turn/end must close the turn");
        Assert.Equal("turn/end", types[^1], "turn/end must be the last event");
        Assert.Equal(2, h.Mock.CallCount, "the mock provider must serve exactly two streams (tool call, then text)");
        Assert.True(((TurnEndEvent)agent.Session.Events[^1]).Reason is CompletedReason, "the turn must end completed");

        var toolCall = agent.Session.Events.OfType<ToolCallEvent>().Single();
        Assert.Equal("todo_write", toolCall.Name, "the tool call must target todo_write");
        var assistants = agent.Session.Events.OfType<AssistantMessageEvent>().Select(evt => evt.Message).ToArray();
        Assert.Equal(2, assistants.Length, "two assistant messages must be logged");
        Assert.True(assistants[0].Content.Any(block => block is ToolCallBlock), "the first assistant message must carry the tool call");
        Assert.True(assistants[1].Content.All(block => block is TextBlock), "the second assistant message must be plain text");

        Assert.Equal(1, turnStoppingCount, "agent/turn-stopping must be emitted once before turn/end");
        Assert.False(loop.IsRunning, "the loop must be idle after the turn");
        Assert.Equal(AgentStatus.Idle, agent.Status, "the agent must return to idle");
        Assert.False(agent.Inbox.HasPending, "the inbox must be drained");

        var stored = h.Persistence.Load(agent.Session.Id)
            ?? throw new AssertionException("the persisted log must exist after the turn");
        Assert.Sequence(types, stored.Events.Select(evt => evt.Type).ToArray(), "the persisted JSONL must replay the identical event sequence");
        Assert.Equal(agent.Session.Events.Count, stored.Events.Count, "the persisted log must hold every event");
    }
}
