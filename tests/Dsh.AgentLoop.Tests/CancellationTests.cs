namespace Harness.AgentLoop.Tests;

/// <summary>Cancellation aborts the running turn, finalizes the safe prefix, and quiesces.</summary>
public static class CancellationTests
{
    public static async Task RunAsync(Harness h)
    {
        var provider = new SlowLlmProvider();
        using var registration = h.Llm.RegisterAdapter(new[] { "slow" }, provider);
        var handle = h.Loop.Create(new SessionId("session-cancel"), new AgentOptions { Provider = "slow", Model = "slow-model" });
        var agent = handle.Agent;
        var loop = h.Loop.GetLoop(new SessionId("session-cancel")) ?? throw new AssertionException("no loop published");
        var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = h.Ctx.On("session/event", (Delegate)(Action<Harness.Session.Session, SessionEvent>)((_, evt) =>
        {
            if (evt is AssistantChunkEvent) firstChunk.TrySetResult();
        }));

        loop.Send(Harness.Prompt("stream slowly"), InboxTarget.NextTurn, wakeup: true);
        await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));
        loop.Cancel(new UserCancel());
        await loop.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(((TurnEndEvent)agent.Session.Events[^1]).Reason is AbortedReason, "the cancelled turn must end aborted");
        Assert.True(agent.Session.Events.OfType<AssistantMessageEvent>().Any(evt => evt.Interrupted), "the cancelled stream must finalize its safe prefix as an interrupted assistant message");
        Assert.False(loop.IsRunning, "the loop must quiesce after cancellation");
        Assert.Equal(AgentStatus.Idle, agent.Status, "the agent must return to idle");
        Assert.False(agent.Inbox.HasPending, "cancellation must clear the inbox");
    }
}
