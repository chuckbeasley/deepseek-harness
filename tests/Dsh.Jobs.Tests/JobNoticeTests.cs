using Harness.Cordis.Core;
using Harness.Agent;
using Harness.Jobs;
using Harness.Llm;
using Harness.Session;

namespace Harness.Jobs.Tests;

/// <summary>The tool-jobs completion-notice delivery over the registry and the agent inbox.</summary>
public static class JobNoticeTests
{
    public static void Bound_TruncatesPastTheSummaryCap()
    {
        var shortSummary = "bash pnpm test [status: completed, exit code: 0]";
        Assert.Equal(shortSummary, JobNoticeDelivery.Bound(shortSummary), "a short summary passes through");
        var longSummary = new string('a', 200);
        var bound = JobNoticeDelivery.Bound(longSummary);
        Assert.Equal(120, bound.Length, "the bound is CONTEXT_SUMMARY_MAX_CHARS");
        Assert.True(bound.EndsWith("…"), "the bound ends with the ellipsis marker");
        Assert.Equal(new string('a', 119) + "…", bound, "the bound keeps the first 119 characters");
    }

    public static void Install_DeliversAnUnreportedCompletionIntoTheNextStepInbox()
    {
        var ctx = new Context();
        var registry = new AgentRegistry(ctx);
        var sessions = new SessionStore(ctx);
        var session = sessions.Create();
        var agent = new global::Harness.Agent.Agent(ctx, session);
        var handle = registry.Register(agent);
        var jobs = new LocalJobsProvider(ctx);
        var notice = JobNoticeDelivery.Install(ctx, jobs);
        try
        {
            var id = jobs.Start(new JobStartRequest(
                Kind: "subagent",
                Label: "Observe Claude background diagnostic",
                Run: () => new JobHooks(
                    Cancel: _ => { },
                    Done: Task.FromResult(new JobOutcome(JobStatus.Failed, Detail: "error; diagnostic: Product subagent failure (product: Claude Code; stage: query-run; category: limit)"))),
                OwnerSession: session.Id.Value));
            Assert.Equal(new JobId("subagent-1"), id, "the job mints the subagent id");
            var pending = agent.Inbox.NextStep.ToArray();
            Assert.Equal(1, pending.Length, "the notice lands in the next-step inbox");
            var message = pending[0];
            Assert.Equal("background job subagent-1 (subagent: Observe Claude background diagnostic) finished "
                + "[status: failed, error; diagnostic: Product subagent failure (product: Claude Code; stage: query-run; category: limit)]. "
                + "Read its output with job_output.",
                ((TextBlock)message.Content[0]).Text, "the notice text matches the recorded completion notice");
            Assert.Equal("subagent Observe Claude background diagnostic "
                + "[status: failed, error; diagnostic: Product subagent failure (product: Cl…",
                message.Source is PluginSource source ? source.Summary : null, "the notice summary is the bounded account");
            Assert.Equal("tool-jobs", message.Source is PluginSource plugin ? plugin.Plugin : null, "the notice is a tool-jobs plugin message");
            Assert.Equal("notice", message.Source is PluginSource form ? form.Form : null, "the notice form is notice");
        }
        finally
        {
            notice.Dispose();
            handle.Dispose();
            ctx.Dispose();
        }
    }

    public static void Install_SkipsAJobTheWaitAlreadyReported()
    {
        var ctx = new Context();
        var registry = new AgentRegistry(ctx);
        var sessions = new SessionStore(ctx);
        var session = sessions.Create();
        var agent = new global::Harness.Agent.Agent(ctx, session);
        var handle = registry.Register(agent);
        var jobs = new LocalJobsProvider(ctx);
        var notice = JobNoticeDelivery.Install(ctx, jobs);
        try
        {
            var id = jobs.Start(new JobStartRequest(
                Kind: "subagent",
                Label: "delayed",
                Run: () => new JobHooks(
                    Cancel: _ => { },
                    Done: Task.Delay(100).ContinueWith(_ => new JobOutcome(JobStatus.Failed, Detail: "aborted; diagnostic: ACP unattended decision (policy: reject; request: execute; decision: denied)"))),
                OwnerSession: session.Id.Value));
            // A pending wait at settlement reports the job; the redundant notice is suppressed.
            jobs.WaitAsync(id, 10_000, session.Id.Value).GetAwaiter().GetResult();
            Assert.Equal(0, agent.Inbox.NextStep.Count, "no notice reaches an already-reported settlement");
        }
        finally
        {
            notice.Dispose();
            handle.Dispose();
            ctx.Dispose();
        }
    }
}
