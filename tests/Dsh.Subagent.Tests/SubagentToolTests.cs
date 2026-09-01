using System.Text.Json;
using Cordis.Core;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Subagent;
using Dsh.Tools;

namespace Dsh.Subagent.Tests;

/// <summary>
/// The deterministic snapshot providers and the subagent tool over them: the recorded
/// foreground failure texts, the background job settlement, and the publish-failure gate.
/// </summary>
public static class SubagentToolTests
{
    public static async Task DiagnosticProvider_AnswersTheRecordedFailures()
    {
        var service = new InProcessSubagentProvider(new Context());
        service.RegisterProvider(new DiagnosticSnapshotProvider());
        var runs = new[]
        {
            ("partial assistant text", "Product subagent failure (product: Claude Code; stage: query-run; category: limit)"),
            ("", "Product subagent failure (product: Claude Code; stage: query-run; category: limit)"),
            ("partial assistant text", "Product subagent failure (product: Codex; stage: turn; category: transport; HTTP status: 503)"),
            ("", "Product subagent failure (product: Codex; stage: turn; category: transport; HTTP status: 503)"),
        };
        for (var index = 0; index < runs.Length; index++)
        {
            var run = await service.StartAsync("snapshot-diagnostic", new SubagentRequest("task"));
            var result = await run.Result;
            Assert.Equal(runs[index].Item1, result.Text, "the recorded partial output returns");
            Assert.Equal(runs[index].Item2, result.Diagnostic, "the recorded diagnostic returns");
            Assert.Equal(SubagentStopReason.Error, result.StopReason, "every recorded run fails");
        }
        await AssertThrowsAsync<InvalidOperationException>(() => service.StartAsync("snapshot-diagnostic", new SubagentRequest("fifth")),
            "a fifth start fails loud");
    }

    public static async Task Foreground_Failure_RendersTheRecordedCodexText()
    {
        var (tools, session) = CreateHarness(out var service, out var jobs);
        service.RegisterProvider(new DiagnosticSnapshotProvider());
        tools.Register(SubagentTool.Definition(service, "snapshot-diagnostic", "subagent_codex", jobs));
        var execution = await Call(tools, "subagent_codex", new { description = "Observe Claude foreground diagnostic", prompt = "Return the Claude diagnostic failure.", run_in_background = false }, session);
        Assert.True(execution.IsError, "the foreground run fails");
        Assert.Equal("subagent run failed\nDiagnostic: Product subagent failure (product: Claude Code; stage: query-run; category: limit)\nPartial output before the run ended:\npartial assistant text",
            ((ToolExecutionFailure)execution).Error.Message, "the failure carries the recorded stop reason, diagnostic, and partial output");
    }

    public static async Task AcpForeground_Failure_RendersTheRecordedText()
    {
        var (tools, session) = CreateHarness(out var service, out var jobs);
        service.RegisterProvider(new AcpSnapshotProvider());
        tools.Register(SubagentTool.Definition(service, "acp-diagnostic", "subagent_acp", jobs));
        var execution = await Call(tools, "subagent_acp", new { description = "Observe ACP foreground failure", prompt = "Return the scripted ACP failure.", run_in_background = false }, session);
        Assert.True(execution.IsError, "the foreground run fails");
        Assert.Equal("subagent run was cancelled\nDiagnostic: ACP unattended decision (policy: reject; request: execute; decision: denied)",
            ((ToolExecutionFailure)execution).Error.Message, "the ACP refusal carries the unattended-decision diagnostic");
    }

    public static async Task Background_ReturnsTheJobAndSettlesFailedWithTheDetail()
    {
        var (tools, session) = CreateHarness(out var service, out var jobs);
        service.RegisterProvider(new DiagnosticSnapshotProvider());
        tools.Register(SubagentTool.Definition(service, "snapshot-diagnostic", "subagent_codex", jobs));
        var execution = await Call(tools, "subagent_codex", new { description = "Observe Codex background diagnostic", prompt = "Return the Codex diagnostic failure.", run_in_background = true }, session);
        Assert.False(execution.IsError, "the background start succeeds");
        var success = (ToolExecutionSuccess)execution;
        Assert.Equal("started background subagent job subagent-1", ((TextBlock)success.Content[0]).Text, "the background render returns the job id");
        var snapshot = await jobs.WaitAsync(new JobId("subagent-1"), 10_000, session.Id.Value);
        Assert.Equal(JobStatus.Failed, snapshot.Status, "the job settles failed");
        Assert.Equal("error; diagnostic: Product subagent failure (product: Claude Code; stage: query-run; category: limit)",
            snapshot.Detail, "the job detail carries the settleRun failure detail");
        Assert.Equal(null, success.Meta, "the subagent tool persists no meta");
    }

    public static void PublishedFailure_ThrowsTheRecordedError()
    {
        var previous = Environment.GetEnvironmentVariable(SubagentTool.PublishedFailureEnv);
        Environment.SetEnvironmentVariable(SubagentTool.PublishedFailureEnv, "1");
        try
        {
            var ctx = new Context();
            var service = new InProcessSubagentProvider(ctx, (_, _) => throw new InvalidOperationException("snapshot published run failed"));
            var jobs = new LocalJobsProvider(ctx);
            var tools = new ToolRuntime(ctx);
            var session = new SessionStore(ctx).Create();
            tools.Register(SubagentTool.Definition(service, "subagent", null, jobs));
            var execution = Call(tools, "subagent", new { description = "Fail published run", prompt = "This child prompt must never run.", run_in_background = false }, session).GetAwaiter().GetResult();
            Assert.True(execution.IsError, "the publish failure fails the run");
            Assert.Equal("subagent run failed: Error: snapshot published run failed; dispose failed: Error: snapshot published handle disposal failed",
                ((ToolExecutionFailure)execution).Error.Message, "the recorded publish + dispose failure text returns");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SubagentTool.PublishedFailureEnv, previous);
        }
    }

    private static (ToolRuntime Tools, Dsh.Session.Session Session) CreateHarness(out InProcessSubagentProvider service, out LocalJobsProvider jobs)
    {
        var ctx = new Context();
        service = new InProcessSubagentProvider(ctx);
        jobs = new LocalJobsProvider(ctx);
        var tools = new ToolRuntime(ctx);
        var store = new SessionStore(ctx);
        var session = store.Create();
        return (tools, session);
    }

    private static async Task<ToolExecutionResult> Call(ToolRuntime tools, string name, object args, Dsh.Session.Session session)
    {
        var tool = tools.Get(name) ?? throw new InvalidOperationException($"tool {name} is not registered");
        return await tools.ExecuteAsync(
            new ToolExecutionInput(new ToolCallId("call"), name, JsonSerializer.SerializeToElement(args), CancellationToken.None) { Session = session },
            CancellationToken.None);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
            Assert.True(false, message);
        }
        catch (TException)
        {
        }
    }
}