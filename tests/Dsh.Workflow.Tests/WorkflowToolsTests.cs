using System.Text.Json;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;
using Dsh.Workflow;

namespace Dsh.Workflow.Tests;

/// <summary>
/// The <c>workflow</c> tool executed through <see cref="ToolRuntime"/>: running a registered
/// definition, mapping non-completed stop reasons to errors, the durable run records, and the
/// result renderer.
/// </summary>
public static class WorkflowToolsTests
{
    private static JsonElement Args(object arguments)
        => JsonSerializer.SerializeToElement(arguments);

    private static ToolRuntime Boot(Context ctx, Dsh.Session.Session? session, out WorkerThreadWorkflowProvider workflow)
    {
        var tools = new ToolRuntime(ctx);
        workflow = new WorkerThreadWorkflowProvider(ctx);
        tools.Register(WorkflowTools.Definition(ctx));
        return tools;
    }

    private static ToolExecutionInput Input(string callId, JsonElement args, Dsh.Session.Session? session = null)
        => new(new ToolCallId(callId), "workflow", args, CancellationToken.None) { Session = session };

    public static void WorkflowTool_ExecutesRegisteredDefinition_AndRecordsTheRun()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var session = sessions.Create(new SessionId("wf-test"));
        var tools = Boot(ctx, session, out var workflow);
        using var registration = workflow.Register(new WorkflowDefinition(
            new WorkflowMeta("math", "adds one to the input"),
            new[] { (WorkflowStep)((context, ct) =>
            {
                var args = (JsonElement)context.Args!;
                return Task.FromResult<object?>(args.GetProperty("x").GetInt32() + 1);
            }) }));

        var result = tools.ExecuteAsync(
            Input("call-1", Args(new { definition = "math", args = new { x = 41 } }), session),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.IsError, "a completed run is a successful tool result");
        var value = Assert.IsType<ToolExecutionSuccess>(result).Value;
        Assert.True(value.GetProperty("runId").GetString()!.Length > 0, "the output carries the minted run id");
        Assert.Equal(1, value.GetProperty("stepsStarted").GetInt32());
        Assert.Equal(42, value.GetProperty("result").GetInt32(), "the final step value is the tool result");

        var rendered = Assert.IsType<Llm.TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("workflow \"math\" completed (1 step).", rendered);
        Assert.Contains("42", rendered);

        var eventTypes = session.Events.Select(evt => evt.Type).ToArray();
        Assert.Equal(new[] { "tool-workflow/run-start", "tool-workflow/run-end" }, eventTypes, "the durable records open and close the run");
        var end = Assert.Single(session.Events.OfType<ToolWorkflowRunEndEvent>());
        Assert.Equal(WorkflowStopReason.Completed, end.StopReason);
        var start = Assert.Single(session.Events.OfType<ToolWorkflowRunStartEvent>());
        Assert.Equal("math", start.Name);
    }

    public static void WorkflowTool_ErrorStep_IsAnErrorResult()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var session = sessions.Create(new SessionId("wf-error"));
        var tools = Boot(ctx, session, out var workflow);
        using var registration = workflow.Register(new WorkflowDefinition(
            new WorkflowMeta("boom", "always throws"),
            new[] { (WorkflowStep)((_, _) => throw new InvalidOperationException("kaboom")) }));

        var result = tools.ExecuteAsync(
            Input("call-1", Args(new { definition = "boom" }), session),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.IsError, "a non-completed stop reason maps to an error result");
        var text = Assert.IsType<Llm.TextBlock>(result.Content[0]).Text;
        Assert.Contains("workflow run failed: kaboom", text);
        Assert.True(Assert.IsType<ToolExecutionFailure>(result).Error.Message.Contains("kaboom", StringComparison.Ordinal));

        var end = Assert.Single(session.Events.OfType<ToolWorkflowRunEndEvent>());
        Assert.Equal(WorkflowStopReason.Error, end.StopReason, "the failed run still closes its durable record");
    }

    public static void WorkflowTool_UnknownDefinition_FailsLoud()
    {
        using var ctx = new Context();
        var tools = Boot(ctx, session: null, out _);

        var result = tools.ExecuteAsync(
            Input("call-1", Args(new { definition = "missing" })),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.IsError);
        Assert.Contains("is not registered", Assert.IsType<Llm.TextBlock>(result.Content[0]).Text);
    }

    public static void WorkflowTool_EmptyDefinitionName_FailsLoud()
    {
        using var ctx = new Context();
        var tools = Boot(ctx, session: null, out _);

        var result = tools.ExecuteAsync(
            Input("call-1", Args(new { definition = "" })),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.IsError);
        Assert.Contains("definition", Assert.IsType<Llm.TextBlock>(result.Content[0]).Text);
    }

    public static void StopReasonError_MapsEveryNonCompletedReason()
    {
        Assert.Null(WorkflowTools.StopReasonError(new WorkflowResult(null, WorkflowStopReason.Completed)));
        Assert.Contains("was cancelled", WorkflowTools.StopReasonError(new WorkflowResult(null, WorkflowStopReason.Cancelled, Error: "nope"))!);
        Assert.Contains("run failed: nope", WorkflowTools.StopReasonError(new WorkflowResult(null, WorkflowStopReason.Error, Error: "nope"))!);
    }

    public static void RenderResult_CapsLongValues()
    {
        var args = Args(new { definition = "big" });
        var value = JsonSerializer.SerializeToElement(new { stepsStarted = 2, result = new string('x', 100) });
        var rendered = WorkflowTools.RenderResult(args, value, maxResultChars: 40);
        const string marker = "Return value:\n";
        var valueRegion = rendered[(rendered.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        Assert.True(valueRegion.Length <= 40 + 64, $"the rendered value region ({valueRegion.Length}) is clipped near the cap");
        Assert.Contains("… [truncated:", valueRegion, "the clipped value carries the truncation notice");
        Assert.Contains("workflow \"big\" completed (2 steps).", rendered);
    }
}
