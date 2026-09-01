namespace Harness.AgentLoop.Tests;

/// <summary>A pre-step listener may reject the turn before any model call or step boundary.</summary>
public static class PreStepTests
{
    public static async Task RunAsync(Harness h)
    {
        var (_, agent, loop) = h.CreateAgent("session-prestep");
        using var rejector = h.Ctx.On("agent/pre-step",
            new Func<PreStepProposal, Func<Task<PreStepDecision>>, Task<PreStepDecision>>(
                (_, _) => Task.FromResult<PreStepDecision>(RejectDecision.Instance)));

        loop.Send(Harness.Prompt("go"), InboxTarget.NextTurn, wakeup: true);
        await loop.WhenIdleAsync();

        Assert.Equal(0, h.Mock.CallCount, "a rejected pre-step must spend no model call");
        var types = agent.Session.Events.Select(evt => evt.Type).ToArray();
        Assert.Sequence(new[] { "agent/inbox/spliced", "turn/start", "agent/inbox/spliced", "turn/end" }, types,
            "a rejected turn logs the durable inbox splices and only the turn boundaries");
        Assert.True(((TurnEndEvent)agent.Session.Events[^1]).Reason is BlockedReason, "the turn must end blocked");
        Assert.False(loop.IsRunning, "the loop must be idle after the rejected turn");
    }
}
