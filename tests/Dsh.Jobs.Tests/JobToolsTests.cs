using System.Text.Json;
using Cordis.Core;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Jobs.Tests;

/// <summary>
/// The job tools executed through <see cref="ToolRuntime"/>: job_output's consuming reads and
/// bounded waits, job_kill's cancellation surface, and job_list's owned projection.
/// </summary>
public static class JobToolsTests
{
    private static ToolExecutionInput Input(string callId, string name, JsonElement args, Dsh.Session.Session? session = null)
        => new(new ToolCallId(callId), name, args, CancellationToken.None) { Session = session };

    private static JsonElement Args(object arguments)
        => JsonSerializer.SerializeToElement(arguments);

    private static ToolRuntime BootTools(Context ctx, out LocalJobsProvider jobs)
    {
        var tools = new ToolRuntime(ctx);
        jobs = new LocalJobsProvider(ctx);
        tools.Register(JobTools.JobOutputDefinition(ctx));
        tools.Register(JobTools.JobListDefinition(ctx));
        tools.Register(JobTools.JobKillDefinition(ctx));
        return tools;
    }

    public static void JobOutput_ReadsIncrementallyThroughTheTool()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out var jobs);
        var queue = new Queue<string>(new[] { "first", "second" });
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = jobs.Start(new JobStartRequest("test", "streaming", () => new JobHooks(
            _ => { },
            done.Task,
            ReadOutput: () => queue.Count > 0 ? queue.Dequeue() : string.Empty)));

        var first = tools.ExecuteAsync(Input("call-1", "job_output", Args(new { job_id = id.Value })), CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(first.IsError);
        var success = Assert.IsType<ToolExecutionSuccess>(first);
        Assert.Equal("first", success.Value.GetProperty("text").GetString());
        Assert.Equal("running", success.Value.GetProperty("job").GetProperty("status").GetString());

        var second = tools.ExecuteAsync(Input("call-2", "job_output", Args(new { job_id = id.Value })), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal("second", Assert.IsType<ToolExecutionSuccess>(second).Value.GetProperty("text").GetString());

        var rendered = Assert.IsType<Llm.TextBlock>(Assert.Single(success.Content)).Text;
        Assert.Contains("[status: running]", rendered, "the rendered text ends with the status line");

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
    }

    public static void JobOutput_WaitTrue_ReturnsTerminalState()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out var jobs);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = jobs.Start(new JobStartRequest("test", "finishes", () => new JobHooks(_ => { }, done.Task)));

        _ = Task.Run(() =>
        {
            Thread.Sleep(50);
            done.TrySetResult(new JobOutcome(JobStatus.Completed, Output: "all done"));
        });

        var result = tools.ExecuteAsync(
            Input("call-1", "job_output", Args(new { job_id = id.Value, wait = true, timeout_ms = 5000 })),
            CancellationToken.None).GetAwaiter().GetResult();

        var success = Assert.IsType<ToolExecutionSuccess>(result);
        Assert.Equal("all done", success.Value.GetProperty("text").GetString());
        Assert.Equal("completed", success.Value.GetProperty("job").GetProperty("status").GetString());
    }

    public static void JobOutput_WaitTrue_TimesOutWithLiveState()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out var jobs);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = jobs.Start(new JobStartRequest("test", "slow", () => new JobHooks(_ => { }, done.Task)));

        var result = tools.ExecuteAsync(
            Input("call-1", "job_output", Args(new { job_id = id.Value, wait = true, timeout_ms = 50 })),
            CancellationToken.None).GetAwaiter().GetResult();

        var success = Assert.IsType<ToolExecutionSuccess>(result);
        Assert.Equal("running", success.Value.GetProperty("job").GetProperty("status").GetString(), "a timed-out wait returns live state, not an error");

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
    }

    public static void JobKill_RequestsCancellationAndReportsAlreadyFinished()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out var jobs);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = jobs.Start(new JobStartRequest("test", "long", () => new JobHooks(
            reason => done.TrySetResult(new JobOutcome(JobStatus.Killed, Detail: $"cancelled: {reason}")),
            done.Task)));

        var result = tools.ExecuteAsync(
            Input("call-1", "job_kill", Args(new { job_id = id.Value, reason = "not needed" })),
            CancellationToken.None).GetAwaiter().GetResult();

        var success = Assert.IsType<ToolExecutionSuccess>(result);
        Assert.Equal("cancellation-requested", success.Value.GetProperty("outcome").GetString());
        Assert.Equal("stopping", success.Value.GetProperty("job").GetProperty("status").GetString());
        var rendered = Assert.IsType<Llm.TextBlock>(Assert.Single(success.Content)).Text;
        Assert.Contains("requested cancellation of job test-1", rendered, "the render names the killed job");

        Assert.Equal(JobStatus.Killed, jobs.WaitAsync(id, 5000).GetAwaiter().GetResult().Status);

        var again = tools.ExecuteAsync(
            Input("call-2", "job_kill", Args(new { job_id = id.Value })),
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal("already-finished", Assert.IsType<ToolExecutionSuccess>(again).Value.GetProperty("outcome").GetString());
    }

    public static void JobList_ListsOwnedJobs()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out var jobs);
        var sessions = new SessionStore(ctx);
        var session = sessions.Create(new SessionId("session-a"));
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Start(new JobStartRequest("test", "alpha", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-a"));
        jobs.Start(new JobStartRequest("test", "beta", () => new JobHooks(_ => { }, done.Task)));

        var result = tools.ExecuteAsync(
            Input("call-1", "job_list", Args(new { }), session),
            CancellationToken.None).GetAwaiter().GetResult();

        var success = Assert.IsType<ToolExecutionSuccess>(result);
        var items = success.Value.EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { "test-1", "test-2" }, items);
        Assert.Equal("alpha", success.Value[0].GetProperty("label").GetString());
        var rendered = Assert.IsType<Llm.TextBlock>(Assert.Single(success.Content)).Text;
        Assert.Contains("test-1 [test] running — alpha", rendered, "the list render shows id, kind, status, and label");

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
    }

    public static void JobOutput_UnknownJob_FailsLoud()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out _);

        var result = tools.ExecuteAsync(Input("call-1", "job_output", Args(new { job_id = "nope-1" })), CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.IsError, "an unknown job id is an error result");
        Assert.Contains("unknown job nope-1", Assert.IsType<Llm.TextBlock>(result.Content[0]).Text);
    }

    public static void JobOutput_EmptyJobId_FailsLoud()
    {
        using var ctx = new Context();
        var tools = BootTools(ctx, out _);

        var result = tools.ExecuteAsync(Input("call-1", "job_output", Args(new { job_id = "" })), CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.IsError);
        Assert.Contains("invalid job_id", Assert.IsType<Llm.TextBlock>(result.Content[0]).Text);
    }
}
