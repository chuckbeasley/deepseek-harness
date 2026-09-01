using Harness.Cordis.Core;
using Harness.Interaction;
using Harness.Session;

namespace Harness.Interaction.Tests;

/// <summary>Fixture helpers: a live agent on a fresh session with an open turn.</summary>
internal static class Fixture
{
    public static (Context Ctx, global::Harness.Agent.Agent Agent, global::Harness.Session.Session Session) OpenTurnAgent(string id = "agent-1")
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);
        var session = store.Create(new SessionId(id));
        session.Append(new TurnStartEvent { Turn = 1 });
        var agent = new global::Harness.Agent.Agent(ctx, session);
        return (ctx, agent, session);
    }
}

/// <summary>
/// The approval seam: the open-turn precondition, the audit pair, the deterministic policies, the
/// fail-closed answerer phase, and the outcome vocabulary.
/// </summary>
public static class ApprovalServiceTests
{
    public static void AskOutsideAnOpenTurn_RejectsBeforeAppendingAnything()
    {
        var ctx = new Context();
        try
        {
            var store = new SessionStore(ctx);
            var session = store.Create(new SessionId("idle"));
            var agent = new global::Harness.Agent.Agent(ctx, session);
            var approval = new ApprovalService(ctx);
            var error = Assert.Throws<InvalidOperationException>(
                () => approval.AskAsync(new ApprovalRequest(agent, "shell/run")).GetAwaiter().GetResult(),
                "an idle ask must reject");
            Assert.Contains("outside an open turn", error.Message, "the failure names the turn rule");
            Assert.True(session.Events.Count == 0, "nothing was appended before the rejection");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void AskAuditsThePairAndFailsClosedWithoutAnAnswerer()
    {
        var (ctx, agent, session) = Fixture.OpenTurnAgent();
        try
        {
            var approval = new ApprovalService(ctx);
            var outcome = approval.AskAsync(new ApprovalRequest(agent, "shell/run", Reason: "needs the sandbox")).GetAwaiter().GetResult();
            Assert.Equal(ApprovalOutcome.Unavailable, outcome, "no answerer fails closed");
            var asked = session.Events.OfType<ApprovalAskedEvent>().Single();
            Assert.Equal("shell/run", asked.ToolName, "the audit names the tool");
            Assert.Equal("needs the sandbox", asked.Reason, "the audit carries the reason");
            var decided = session.Events.OfType<ApprovalDecidedEvent>().Single();
            Assert.Equal(asked.Id, decided.Id, "the pair shares one identity");
            Assert.Equal(ApprovalOutcome.Unavailable, decided.Outcome, "the decided audit matches the outcome");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void AnAnswererOnTheWaterfallDecidesTheAsk()
    {
        var (ctx, agent, session) = Fixture.OpenTurnAgent();
        try
        {
            var approval = new ApprovalService(ctx);
            var registration = ctx.On("approval/request",
                new Func<ApprovalRequest, Func<Task<ApprovalOutcome>>, Task<ApprovalOutcome>>((request, next) =>
                    Task.FromResult(request.ToolName == "shell/run" ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected)));
            try
            {
                Assert.Equal(ApprovalOutcome.AllowedOnce,
                    approval.AskAsync(new ApprovalRequest(agent, "shell/run")).GetAwaiter().GetResult(), "the answerer grants");
                Assert.Equal(ApprovalOutcome.Rejected,
                    approval.AskAsync(new ApprovalRequest(agent, "fs/write")).GetAwaiter().GetResult(), "the answerer refuses");
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void NeverPolicy_RejectsDeterministically_BeforeAnyDispatch()
    {
        var (ctx, agent, session) = Fixture.OpenTurnAgent();
        try
        {
            var approval = new ApprovalService(ctx, ApprovalPolicy.Never);
            var answered = 0;
            var registration = ctx.On("approval/request",
                new Func<ApprovalRequest, Func<Task<ApprovalOutcome>>, Task<ApprovalOutcome>>((request, next) =>
                {
                    answered++;
                    return Task.FromResult(ApprovalOutcome.AllowedOnce);
                }));
            try
            {
                Assert.Equal(ApprovalOutcome.Rejected,
                    approval.AskAsync(new ApprovalRequest(agent, "shell/run")).GetAwaiter().GetResult(), "never rejects");
                Assert.Equal(0, answered, "the answerer never saw the ask");
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void SessionOverride_BeatsTheConfiguredDefault()
    {
        var (ctx, agent, session) = Fixture.OpenTurnAgent();
        try
        {
            var approval = new ApprovalService(ctx, ApprovalPolicy.Ask);
            Assert.Equal(ApprovalPolicy.Ask, approval.EffectivePolicy(session), "the default applies without an override");
            approval.SetPolicy(agent, ApprovalPolicy.Never);
            Assert.Equal(ApprovalPolicy.Never, approval.EffectivePolicy(session), "the override applies");
            Assert.True(session.Events.OfType<ApprovalPolicyEvent>().Count() == 1, "the override is logged once");
            approval.SetPolicy(agent, ApprovalPolicy.Never);
            Assert.True(session.Events.OfType<ApprovalPolicyEvent>().Count() == 1, "an unchanged switch appends nothing");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void AnAbortedAsk_SettlesCancelled_AndDiscardsTheLateAnswer()
    {
        var (ctx, agent, session) = Fixture.OpenTurnAgent();
        try
        {
            var approval = new ApprovalService(ctx);
            using var cts = new CancellationTokenSource();
            var slow = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ctx.On("approval/request",
                new Func<ApprovalRequest, Func<Task<ApprovalOutcome>>, Task<ApprovalOutcome>>((request, next) => slow.Task));
            try
            {
                var ask = approval.AskAsync(new ApprovalRequest(agent, "shell/run", CancellationToken: cts.Token));
                cts.Cancel();
                Assert.Equal(ApprovalOutcome.Cancelled, ask.GetAwaiter().GetResult(), "the abort settles cancelled");
                slow.TrySetResult(ApprovalOutcome.AllowedOnce);
                Assert.Equal(ApprovalOutcome.Cancelled,
                    session.Events.OfType<ApprovalDecidedEvent>().Single().Outcome, "the late answer is discarded");
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void AThrowingAnswerer_FailsTheAskClosed()
    {
        var (ctx, agent, session) = Fixture.OpenTurnAgent();
        try
        {
            var approval = new ApprovalService(ctx);
            var registration = ctx.On("approval/request",
                new Func<ApprovalRequest, Func<Task<ApprovalOutcome>>, Task<ApprovalOutcome>>((request, next) =>
                    throw new InvalidOperationException("answerer bug")));
            try
            {
                Assert.Equal(ApprovalOutcome.Unavailable,
                    approval.AskAsync(new ApprovalRequest(agent, "shell/run")).GetAwaiter().GetResult(),
                    "a throwing answerer fails the ask closed, not the caller");
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }
}
