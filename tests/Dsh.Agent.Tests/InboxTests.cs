using Dsh.Llm;

namespace Dsh.Agent.Tests;

/// <summary>Inbox claim order, notifications, and identity validation.</summary>
internal static class InboxTests
{
    public static void ClaimReturnsQueuedInOrder()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m1", "one"));
        agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m2", "two"));
        var claimed = agent.Inbox.Claim(InboxTarget.NextStep, turn: 1);
        Assert.Equal(2, claimed.Count);
        Assert.Equal("m1", claimed[0].Id.Value);
        Assert.Equal("m2", claimed[1].Id.Value);
        Assert.Equal(0, agent.Inbox.NextStep.Count);
    }

    public static void ClaimNextTurnPopsOneQueuedTurn()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        agent.Inbox.Append(InboxTarget.NextTurn, Harness.Msg("m1", "one"));
        agent.Inbox.Append(InboxTarget.NextTurn, Harness.Msg("m2", "two"));
        var first = agent.Inbox.Claim(InboxTarget.NextTurn, turn: 1);
        var second = agent.Inbox.Claim(InboxTarget.NextTurn, turn: 2);
        var third = agent.Inbox.Claim(InboxTarget.NextTurn, turn: 3);
        Assert.Equal(1, first.Count);
        Assert.Equal("m1", first[0].Id.Value);
        Assert.Equal(1, second.Count);
        Assert.Equal("m2", second[0].Id.Value);
        Assert.Equal(0, third.Count);
    }

    public static void ClaimEmptyReturnsNone()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        Assert.Equal(0, agent.Inbox.Claim(InboxTarget.NextStep, turn: 1).Count);
        Assert.True(!agent.Inbox.HasPending, "an empty inbox must not have pending work");
    }

    public static void ClaimPublishesClaimedEvents()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var claimed = new List<AgentInboxClaimedPayload>();
        ctx.On<AgentInboxClaimedPayload>(AgentEvents.InboxClaimed, claimed.Add);
        agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m1", "one"));
        agent.Inbox.Append(InboxTarget.NextTurn, Harness.Msg("m2", "two"));
        agent.Inbox.Claim(InboxTarget.NextTurn, turn: 7);
        Assert.Equal(2, claimed.Count);
        Assert.Equal("m1", claimed[0].Message.Id.Value);
        Assert.Equal("m2", claimed[1].Message.Id.Value);
        Assert.Equal(7L, claimed[0].Turn);
        Assert.Equal(7L, claimed[1].Turn);
    }

    public static void InsertedAndDiscardedNotifications()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var inserted = new List<AgentInboxInsertedPayload>();
        var discarded = new List<AgentInboxDiscardedPayload>();
        ctx.On<AgentInboxInsertedPayload>(AgentEvents.InboxInserted, inserted.Add);
        ctx.On<AgentInboxDiscardedPayload>(AgentEvents.InboxDiscarded, discarded.Add);
        agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m1", "one"));
        agent.Inbox.Append(InboxTarget.NextTurn, Harness.Msg("m2", "two"));
        Assert.Equal(2, inserted.Count);
        agent.Inbox.Clear();
        Assert.Equal(2, discarded.Count);
        Assert.True(!agent.Inbox.HasPending, "clear must empty both lists");
    }

    public static void DuplicateIdentityThrows()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        agent.Inbox.Append(InboxTarget.NextTurn, Harness.Msg("m1", "one"));
        Assert.Throws<InvalidOperationException>(
            () => agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m1", "again")),
            "a pending identity must not be inserted twice across either list");
    }

    public static void ReplaceAndRemove()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var discarded = new List<AgentInboxDiscardedPayload>();
        var inserted = new List<AgentInboxInsertedPayload>();
        ctx.On<AgentInboxDiscardedPayload>(AgentEvents.InboxDiscarded, discarded.Add);
        ctx.On<AgentInboxInsertedPayload>(AgentEvents.InboxInserted, inserted.Add);
        agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m1", "one"));
        inserted.Clear();
        var replacement = Harness.Msg("m2", "two");
        Assert.True(agent.Inbox.Replace(new MessageId("m1"), replacement), "replacing a pending message must succeed");
        Assert.Equal("m2", agent.Inbox.NextStep[0].Id.Value);
        Assert.Equal(1, discarded.Count);
        Assert.Equal(1, inserted.Count);
        Assert.True(!agent.Inbox.Replace(new MessageId("m1"), replacement), "replacing a removed message must fail");
        Assert.True(agent.Inbox.Remove(new MessageId("m2")), "removing a pending message must succeed");
        Assert.Equal(0, agent.Inbox.NextStep.Count);
    }

    public static void ConfigCapRejectsOverflow()
    {
        var (ctx, _, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1", config: new AgentConfig { MaxPendingMessages = 1 });
        agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m1", "one"));
        Assert.Throws<InvalidOperationException>(
            () => agent.Inbox.Append(InboxTarget.NextStep, Harness.Msg("m2", "two")),
            "a configured cap must reject an overflow insertion");
    }
}
