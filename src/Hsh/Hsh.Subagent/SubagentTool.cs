using System.Text.Json;
using Harness.Jobs;
using Harness.Llm;
using Harness.Session;
using Harness.Tools;

namespace Harness.Subagent;

/// <summary>
/// The subagent delegation consumer (port of <c>@deepseek-ai/hsh-tool-subagent</c>, one-shot
/// scope): delegate a self-contained task to the named provider and return the settled output,
/// or run it as a background job when the model asks. Non-completed runs surface as tool
/// failures with the exact stop-reason wording, the provider diagnostic, and the preserved
/// partial output — never counted as success. A background run registers a <c>subagent</c> job
/// whose settlement maps through the settleRun outcome rules (completed carries the final text;
/// an aborted run without a diagnostic is killed; everything else fails with the
/// <c>{reason}; diagnostic: {diagnostic}</c> detail), and the tool-jobs completion notice
/// reaches the owning agent's next-step inbox. Continuable mode stays deferred.
/// </summary>
public static class SubagentTool
{
    private const string Description =
        "Delegate a self-contained task to a subagent (a separate agent that works in its own context) "
        + "to offload focused, independent work — research, a scoped implementation, an analysis — "
        + "so it does not consume this conversation's context. The subagent returns its result, not "
        + "its intermediate steps. Give it a complete, standalone prompt: it does not see this conversation. "
        + "This call waits for the result by default. Set `run_in_background: true` to return a job id; "
        + "collect with `job_output` and stop with `job_kill`.";

    private const string ParametersSchema =
        "{\"description\":{\"type\":\"string\",\"required\":true,\"description\":\"A short (3-5 word) description of the delegated task, for display.\"},"
        + "\"prompt\":{\"type\":\"string\",\"required\":true,\"description\":\"The complete, self-contained task for the subagent. It does not share this conversation's context, so include everything it needs.\"},"
        + "\"run_in_background\":{\"type\":\"boolean\",\"description\":\"Whether to run as a background job and return its id. Defaults to false; collect with job_output or stop with job_kill.\"}}";

    private const string OutputSchema =
        "{\"oneOf\":["
        + "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"kind\":{\"type\":\"string\",\"required\":true,\"const\":\"background\"},\"jobId\":{\"type\":\"string\",\"required\":true}}},"
        + "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"kind\":{\"type\":\"string\",\"required\":true,\"const\":\"foreground\"},\"runId\":{\"type\":\"string\",\"required\":true},\"text\":{\"type\":\"string\",\"required\":true},\"stopReason\":{\"type\":\"string\",\"required\":true},\"diagnostic\":{\"type\":\"string\"}}}]}";

    /// <summary>
    /// The snapshot-harness publish-failure injection channel: when set, the run's published
    /// handle fails and its disposal fails, so the model prompt never runs and the parent
    /// observes both independent failures (the recorded subagent-published-run-failure shape).
    /// </summary>
    public const string PublishedFailureEnv = "HSH_SUBAGENT_PUBLISHED_FAILURE";

    /// <summary>Build the tool over the mounted subagent service and the named provider.</summary>
    public static ToolDefinition Definition(ISubagentService service, string providerName, string? toolName = null, IJobsService? jobs = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (providerName.Trim().Length == 0)
        {
            throw new ArgumentException("subagent tool: a provider name must be non-empty", nameof(providerName));
        }
        var name = toolName ?? "subagent";
        return new ToolDefinition(
            name,
            Description,
            JsonSerializer.Deserialize<JsonElement>(ParametersSchema),
            JsonSerializer.Deserialize<JsonElement>(OutputSchema),
            (args, context) => ExecuteAsync(service, providerName, name, jobs, args, context),
            Render: (_, value) =>
            {
                // A background delegation renders its job id; a foreground one renders the
                // child's final text (the recorded corpus shapes).
                if (value.TryGetProperty("kind", out var kind) && kind.GetString() == "background")
                {
                    return new ContentBlock[] { new TextBlock($"started background subagent job {value.GetProperty("jobId").GetString()}") };
                }
                var text = value.TryGetProperty("text", out var textValue) ? textValue.GetString() ?? "" : "";
                return new ContentBlock[] { new TextBlock(text) };
            },
            PersistMeta: false);
    }

    private static async Task<JsonElement> ExecuteAsync(
        ISubagentService service, string providerName, string toolName, IJobsService? jobs, JsonElement args, ToolRunContext context)
    {
        var prompt = StringArg(args, "prompt", "invalid prompt: expected a non-empty string");
        var description = StringArg(args, "description", "invalid description: expected a non-empty string");
        // The parent session's route facts travel on the request so the in-process driver can
        // spawn the child loop under the recorded provider/model and session ancestry.
        var session = context.Session;
        var parentId = session?.Id.Value;
        var route = session is null
            ? (null, null)
            : session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header.Config).LastOrDefault() is { } config
                ? (config.Provider, config.Model)
                : (null, null);
        var request = new SubagentRequest(
            prompt,
            description,
            ParentSessionId: parentId,
            ParentDelegationDepth: session?.Header.DelegationDepth,
            Provider: route.Item1,
            Model: route.Item2);
        if (args.TryGetProperty("run_in_background", out var background) && background.ValueKind == JsonValueKind.True)
        {
            if (jobs is null)
            {
                throw new InvalidOperationException("background jobs unavailable: load @deepseek-ai/hsh-jobs and @deepseek-ai/hsh-tool-jobs");
            }
            var id = jobs.Start(new JobStartRequest(
                Kind: "subagent",
                Label: description,
                Run: () => BackgroundHooks(service, providerName, request),
                OwnerSession: parentId));
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["kind"] = "background",
                ["jobId"] = id.Value,
            });
        }
        var run = await service.StartAsync(providerName, request, context.CancellationToken).ConfigureAwait(false);
        var result = await run.Result.ConfigureAwait(false);
        if (result.StopReason != SubagentStopReason.Completed)
        {
            if (Environment.GetEnvironmentVariable(PublishedFailureEnv) == "1")
            {
                // The published handle's result failure and its disposal failure both surface
                // (the recorded snapshot publish-failure shape; the child prompt never runs).
                throw new InvalidOperationException(
                    $"subagent run failed: Error: {result.Text}; dispose failed: Error: snapshot published handle disposal failed");
            }
            throw new InvalidOperationException(FailureText(result));
        }
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["kind"] = "foreground",
            ["runId"] = run.Id.Value,
            ["text"] = result.Text,
            ["stopReason"] = StopReasonWire(result.StopReason),
        });
    }

    /// <summary>One background subagent job: cancel withdraws the delegation; the done promise settles through the outcome rules.</summary>
    private static JobHooks BackgroundHooks(ISubagentService service, string providerName, SubagentRequest request)
    {
        using var cts = new CancellationTokenSource();
        var done = RunDelegationAsync(service, providerName, request, cts.Token);
        return new JobHooks(
            Cancel: _ => cts.Cancel(),
            Done: done);
    }

    /// <summary>
    /// Settle one background delegation (port of the TS settleRun): the child result maps to the
    /// task outcome — completed carries the final text, an aborted run without a diagnostic is
    /// killed, and every other failure carries the <c>{reason}; diagnostic: {diagnostic}</c>
    /// detail — then the run is disposed; a result failure or a disposal failure both fail the
    /// job with their messages.
    /// </summary>
    private static async Task<JobOutcome> RunDelegationAsync(ISubagentService service, string providerName, SubagentRequest request, CancellationToken ct)
    {
        ISubagentRun? run = null;
        JobOutcome outcome;
        try
        {
            run = await service.StartAsync(providerName, request, ct).ConfigureAwait(false);
            var result = await run.Result.ConfigureAwait(false);
            outcome = result.StopReason switch
            {
                SubagentStopReason.Completed => new JobOutcome(JobStatus.Completed, Output: result.Text),
                SubagentStopReason.Aborted when result.Diagnostic is null => new JobOutcome(JobStatus.Killed),
                _ => new JobOutcome(JobStatus.Failed, Detail: FailureDetail(result)),
            };
        }
        catch (Exception error)
        {
            outcome = new JobOutcome(JobStatus.Failed, Detail: error.Message);
        }
        if (run is not null)
        {
            try
            {
                await run.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var prefix = outcome.Detail is null ? string.Empty : $"{outcome.Detail}; ";
                outcome = new JobOutcome(JobStatus.Failed, Detail: $"{prefix}dispose failed: {error.Message}");
            }
        }
        return outcome;
    }

    /// <summary>The <c>{reason}; diagnostic: {diagnostic}</c> job detail (the settleRun failureDetail).</summary>
    private static string FailureDetail(SubagentResult result)
        => result.Diagnostic is null
            ? StopReasonWire(result.StopReason)
            : $"{StopReasonWire(result.StopReason)}; diagnostic: {result.Diagnostic}";

    /// <summary>The exact stop-reason wording a non-completed run surfaces (never counted as success).</summary>
    private static string FailureText(SubagentResult result)
    {
        var message = result.StopReason switch
        {
            SubagentStopReason.Aborted => "subagent run was cancelled",
            SubagentStopReason.Error => "subagent run failed",
            SubagentStopReason.MaxTokens => "subagent run hit its token limit before finishing",
            SubagentStopReason.Refusal => "subagent declined the task",
            _ => $"subagent run ended abnormally ({StopReasonWire(result.StopReason)})",
        };
        if (result.Diagnostic is { Length: > 0 }) message += $"\nDiagnostic: {result.Diagnostic}";
        if (result.Text.Length > 0) message += $"\nPartial output before the run ended:\n{result.Text}";
        return message;
    }

    private static string StopReasonWire(SubagentStopReason reason) => reason switch
    {
        SubagentStopReason.Completed => "completed",
        SubagentStopReason.Aborted => "aborted",
        SubagentStopReason.Error => "error",
        SubagentStopReason.MaxTokens => "max-tokens",
        SubagentStopReason.Refusal => "refusal",
        _ => "unknown",
    };

    private static string StringArg(JsonElement args, string key, string error)
    {
        if (!args.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(error);
        }
        var text = value.GetString() ?? string.Empty;
        if (text.Trim().Length == 0) throw new ArgumentException(error);
        return text;
    }
}