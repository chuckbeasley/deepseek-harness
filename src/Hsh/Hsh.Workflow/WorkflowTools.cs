using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Session;
using Harness.Tools;

namespace Harness.Workflow;

/// <summary>
/// Model-facing Consumer of the workflow capability: the <c>workflow</c> tool over
/// <see cref="IWorkflowService"/>. It starts a registered definition with the model's <c>args</c>,
/// awaits the run's result, and returns the final value; non-completed stop reasons become tool
/// errors. Each top-level call appends the durable <c>tool-workflow/run-start</c> and
/// <c>tool-workflow/run-end</c> records through the owning session.
///
/// Port of <c>@deepseek-ai/hsh-tool-workflow</c>: the <c>script</c>/<c>meta</c> parameters become a
/// single <c>definition</c> name (definitions are host-registered), the output replaces
/// <c>agentsStarted</c> with <c>stepsStarted</c>, and the <c>tool-workflow/agent-*</c> member
/// records are absent (no subagent seam in this wave).
/// </summary>
public static class WorkflowTools
{
    private const string ParametersSchemaJson =
        "{\"definition\":{\"type\":\"string\",\"required\":true,\"description\":\"Name of the registered workflow definition to run (returned by the tool that registered it).\"},\"args\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"Optional JSON input exposed to the workflow steps as the run's args (wrap a bare list as a field, e.g. {\\\"files\\\": [...]}).\"}}";

    private const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"runId\":{\"type\":\"string\",\"required\":true},\"stepsStarted\":{\"type\":\"integer\",\"required\":true},\"result\":{\"type\":\"json\",\"required\":true}}}";

    /// <summary>
    /// The model-facing description (pinned literal). This IS the model-facing spec: how to use the
    /// tool, what a registered definition is, and the step model.
    /// </summary>
    public const string Description =
        "Run a registered workflow definition that orchestrates steps on worker tasks. Use this for work "
        + "that fans out across many independent pieces — an audit over many files, a migration, "
        + "multi-angle research — where the orchestration is written once and re-run with different args. "
        + "The workflow's identity and ordered steps are registered by the host under a short kebab-case "
        + "name; this tool starts a run of that definition with the `args` input. The run executes in the "
        + "foreground: this call returns when the whole workflow finishes. Steps run in order on worker "
        + "tasks, observe the run's cancellation token, and may narrate progress through phase/log. The "
        + "final step's value is JSON data and is this tool's result.";

    /// <summary>
    /// Build the <c>workflow</c> ToolDefinition over the mounted workflow service. Execute starts the
    /// named definition, records the durable run lifecycle, awaits the result, and maps a
    /// non-completed stop reason to a tool error; Render projects the outcome text.
    /// </summary>
    public static ToolDefinition Definition(Context ctx, int maxResultChars = 50_000)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var workflow = ctx.Get<IWorkflowService>("workflow")
            ?? throw new InvalidOperationException("workflow tool: the \"workflow\" service is not mounted");
        SessionEventTypes.Register(ToolWorkflowRunStartEvent.EventTypeName, typeof(ToolWorkflowRunStartEvent));
        SessionEventTypes.Register(ToolWorkflowRunEndEvent.EventTypeName, typeof(ToolWorkflowRunEndEvent));
        return new ToolDefinition(
            Name: "workflow",
            Description: Description,
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, context) => ExecuteAsync(workflow, args, context),
            Render: (args, value) => new ContentBlock[] { new TextBlock(RenderResult(args, value, maxResultChars)) });
    }

    private static async Task<JsonElement> ExecuteAsync(IWorkflowService workflow, JsonElement args, ToolRunContext context)
    {
        var definitionName = args.TryGetProperty("definition", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrEmpty(definitionName))
        {
            throw new ArgumentException("workflow tool: \"definition\" must be a non-empty string");
        }
        JsonElement? argsValue = null;
        if (args.TryGetProperty("args", out var modelArgs) && modelArgs.ValueKind != JsonValueKind.Null)
        {
            argsValue = modelArgs;
        }

        // Meta/definition validation failures throw synchronously here and become error results via
        // the registry — the model sees the violation and can correct the call.
        var run = workflow.Start(new WorkflowRunStartRequest(
            definitionName,
            Args: argsValue,
            ParentSession: context.Session?.Id.Value,
            CancellationToken: context.CancellationToken));
        var recorded = context.Session is not null;
        if (recorded)
        {
            context.Session!.Append(new ToolWorkflowRunStartEvent { RunId = run.Id.Value, Name = run.Meta.Name });
        }
        // Bridge the tool's abort signal to the run: if the calling step is aborted while the
        // workflow is in flight, cancel the whole run.
        using var registration = context.CancellationToken.Register(() => run.Cancel("parent step aborted"));

        WorkflowResult result;
        try
        {
            result = await run.Result.ConfigureAwait(false);
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
        if (recorded)
        {
            context.Session!.Append(new ToolWorkflowRunEndEvent { RunId = run.Id.Value, StopReason = result.StopReason });
        }
        var stopError = StopReasonError(result);
        if (stopError is not null)
        {
            // Map a non-clean finish to an error result (the registry turns a throw into an error).
            // Report the reason, not partial output.
            throw new InvalidOperationException(stopError);
        }
        return JsonSerializer.SerializeToElement(new { runId = run.Id.Value, stepsStarted = result.StepsStarted, result = result.Value });
    }

    /// <summary>A non-completed stop reason means the workflow did not finish cleanly.</summary>
    public static string? StopReasonError(WorkflowResult result) => result.StopReason switch
    {
        WorkflowStopReason.Completed => null,
        WorkflowStopReason.Cancelled => $"workflow run was cancelled{(result.Error is null ? string.Empty : $" ({result.Error})")}",
        WorkflowStopReason.Error => $"workflow run failed: {result.Error ?? "unknown error"}",
        _ => $"workflow run ended abnormally ({result.StopReason})",
    };

    /// <summary>Render the run's outcome text: the definition name, step count, and the JSON value (capped).</summary>
    public static string RenderResult(JsonElement args, JsonElement value, int maxResultChars)
    {
        var name = args.TryGetProperty("definition", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var stepsStarted = value.GetProperty("stepsStarted").GetInt32();
        var rendered = JsonSerializer.Serialize(value.GetProperty("result"), new JsonSerializerOptions { WriteIndented = true });
        var clipped = rendered.Length > maxResultChars
            ? $"{rendered[..maxResultChars]}\n… [truncated: {rendered.Length - maxResultChars} more characters]"
            : rendered;
        return $"workflow \"{name}\" completed ({stepsStarted} step{(stepsStarted == 1 ? string.Empty : "s")}).\nReturn value:\n{clipped}";
    }
}
