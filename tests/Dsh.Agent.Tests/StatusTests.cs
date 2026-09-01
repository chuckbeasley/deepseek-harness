using Harness.Session;

namespace Harness.Agent.Tests;

/// <summary>Status transitions and step transitions emit their typed agent events.</summary>
internal static class StatusTests
{
    public static void InitialStatusIsIdle()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        Assert.Equal(AgentStatus.Idle, agent.Status);
    }

    public static void StatusTransitionsEmitEvents()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var seen = new List<AgentStatusPayload>();
        ctx.On<AgentStatusPayload>(AgentEvents.Status, seen.Add);
        agent.SetStatus(AgentStatus.Running);
        agent.SetStatus(AgentStatus.Idle);
        Assert.Equal(2, seen.Count);
        Assert.Equal(AgentStatus.Running, seen[0].Status);
        Assert.Equal(AgentStatus.Idle, seen[1].Status);
        Assert.True(ReferenceEquals(seen[0].Agent, agent), "status payload must carry the exact agent");
    }

    public static void SameStatusIsNoOp()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var count = 0;
        ctx.On<AgentStatusPayload>(AgentEvents.Status, _ => count++);
        agent.SetStatus(AgentStatus.Idle);
        Assert.Equal(0, count);
    }

    public static void StepTransitionsEmitEvents()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var starts = new List<AgentStepStartPayload>();
        var ends = new List<AgentStepEndPayload>();
        ctx.On<AgentStepStartPayload>(AgentEvents.StepStart, starts.Add);
        ctx.On<AgentStepEndPayload>(AgentEvents.StepEnd, ends.Add);
        agent.StartStep(1);
        agent.EndStep();
        Assert.Equal(1, starts.Count);
        Assert.Equal(1, ends.Count);
        Assert.Equal(1L, starts[0].Turn);
        Assert.Equal(1L, starts[0].Step);
        Assert.Equal(1L, ends[0].Turn);
        Assert.Equal(1L, ends[0].Step);
        agent.StartStep(1);
        Assert.Equal(2L, agent.Step);
    }

    public static void CancelSignalsAndKeepsFirstCause()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        Assert.True(!agent.CancellationToken.IsCancellationRequested, "a fresh agent must not be cancelled");
        agent.Cancel(new ParentCancel());
        agent.Cancel(new UserCancel());
        Assert.True(agent.CancellationToken.IsCancellationRequested, "cancel must signal the lifecycle token");
        Assert.True(agent.LastCancelCause is ParentCancel, "the first cause must win");
    }
}
