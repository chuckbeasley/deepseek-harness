using Harness.Cordis.Core;
using Harness.Agent;
using Harness.Llm;
using Harness.Session;

namespace Harness.Jobs;

/// <summary>
/// Completion-notice delivery for the background-job seam (the port of the dsh-tool-jobs
/// delivery half): when an owned job settles unreported, its owning agent receives one
/// in-session notice in the next-step inbox — <c>background job {id} ({kind}: {label}) finished
/// [status: ...]. Read its output with job_output.</c> The port delivers by injection only (an
/// idle owner is not woken with a follow-up turn, documented reduction), and a settlement that a
/// kill, read, or wait already reported suppresses the redundant notice.
/// </summary>
public static class JobNoticeDelivery
{
    /// <summary>Bound for one <c>notice</c> summary (the TS CONTEXT_SUMMARY_MAX_CHARS).</summary>
    public const int ContextSummaryMaxChars = 120;

    /// <summary>
    /// Install the notice listener over the mounted jobs service. The listener is contained:
    /// a missing agent registry, unknown owner, or throwing delivery cannot break the settlement
    /// that already happened.
    /// </summary>
    public static IDisposable Install(Context ctx, IJobsService jobs)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(jobs);
        var agents = ctx.Get<AgentRegistry>("agents");
        if (agents is null) return new ActionDisposer(() => { });
        return jobs.OnJobDone((snapshot, owner) =>
        {
            if (snapshot.Reported || owner is null) return;
            var agent = agents.Get(new SessionId(owner));
            if (agent is null) return;
            var statusLine = JobTools.StatusLine(snapshot.Status, snapshot.Detail);
            var text = $"background job {snapshot.Id.Value} ({snapshot.Kind}: {snapshot.Label}) finished {statusLine}. Read its output with job_output.";
            var summary = Bound($"{snapshot.Kind} {snapshot.Label} {statusLine}");
            var message = new UserMessage
            {
                Id = new MessageId(Guid.NewGuid().ToString("D")),
                Content = new ContentBlock[] { new TextBlock(text) },
                Source = new PluginSource { Plugin = "tool-jobs", Form = "notice", Summary = summary },
            };
            agent.Inbox.Prepend(InboxTarget.NextStep, message);
        });
    }

    /// <summary>Bound one notice summary to <see cref="ContextSummaryMaxChars"/> characters, ellipsized past the bound.</summary>
    public static string Bound(string summary)
        => summary.Length <= ContextSummaryMaxChars
            ? summary
            : summary[..(ContextSummaryMaxChars - 1)] + "…";
}