using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Jobs;

/// <summary>
/// Model-facing Consumer of the background-job capability: the <c>job_output</c>, <c>job_list</c>,
/// and <c>job_kill</c> tools over <see cref="IJobsService"/>. Reads are consuming (never
/// re-delivered; unread stream output is dropped by the one-per-job cursor). The tools are
/// live-only — unlike the TS consumer they do not append durable session events, because the
/// completion notice in TS is delivered as a live inbox message through the owning agent, and this
/// port has no agent inbox dependency. Port of <c>@deepseek-ai/dsh-tool-jobs</c> minus the
/// completion-notice delivery (no agent inbox seam) and the attached controller (the tools
/// themselves serve as the controller).
/// </summary>
public static class JobTools
{
    /// <summary>Shared schema for job-control outputs (the TS PUBLIC_TASK_SCHEMA).</summary>
    public const string PublicTaskSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"id\":{\"type\":\"string\",\"required\":true},\"kind\":{\"type\":\"string\",\"required\":true},\"label\":{\"type\":\"string\",\"required\":true},\"status\":{\"type\":\"string\",\"required\":true,\"enum\":[\"running\",\"stopping\",\"completed\",\"killed\",\"failed\"]},\"detail\":{\"type\":\"string\"},\"startedAt\":{\"type\":\"integer\",\"required\":true},\"finishedAt\":{\"type\":\"integer\"}}}";

    private const string JobOutputParametersJson =
        "{\"job_id\":{\"type\":\"string\",\"required\":true,\"description\":\"Job id returned by the tool that started the background work.\"},\"wait\":{\"type\":\"boolean\",\"description\":\"Block until the job reaches a terminal status or the timeout expires. A timed-out wait returns [status: running] and leaves the job alive.\"},\"timeout_ms\":{\"type\":\"number\",\"description\":\"Max wait in milliseconds (only meaningful with wait: true). Defaults to the configured wait timeout; capped by the configured maximum.\"}}";

    private static readonly string JobOutputOutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\",\"required\":true},\"job\":" + PublicTaskSchemaJson + ",\"required\":true}}";

    private const string JobKillParametersJson =
        "{\"job_id\":{\"type\":\"string\",\"required\":true,\"description\":\"Job id returned by the tool that started the background work.\"},\"reason\":{\"type\":\"string\",\"description\":\"Optional short reason, recorded in the log and forwarded to the job.\"}}";

    private static readonly string JobKillOutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"outcome\":{\"type\":\"string\",\"required\":true,\"enum\":[\"cancellation-requested\",\"already-finished\"]},\"job\":" + PublicTaskSchemaJson + ",\"required\":true}}";

    private static readonly string JobListOutputSchemaJson =
        "{\"type\":\"array\",\"items\":" + PublicTaskSchemaJson + "}";

    /// <summary>Remove job ownership and notification bookkeeping from a registry snapshot.</summary>
    public static PublicJobSnapshot PublicJob(JobSnapshot snapshot) => new(
        snapshot.Id.Value,
        snapshot.Kind,
        snapshot.Label,
        snapshot.Status,
        snapshot.Detail,
        snapshot.StartedAt,
        snapshot.FinishedAt);

    /// <summary>
    /// Render generic status with optional producer detail (the TS <c>statusLine</c>).
    /// </summary>
    public static string StatusLine(JobStatus status, string? detail)
        => detail is null
            ? $"[status: {JobStatuses.WireName(status)}]"
            : $"[status: {JobStatuses.WireName(status)}, {detail}]";

    /// <summary>The <c>job_output</c> ToolDefinition over the mounted jobs service.</summary>
    public static ToolDefinition JobOutputDefinition(Context ctx, int waitTimeoutMs = 30_000, int maxWaitTimeoutMs = 600_000)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var jobs = ctx.Get<IJobsService>("jobs")
            ?? throw new InvalidOperationException("job_output: the \"jobs\" service is not mounted");
        if (waitTimeoutMs > maxWaitTimeoutMs)
        {
            throw new ArgumentException($"tool-jobs: waitTimeoutMs ({waitTimeoutMs}) exceeds maxWaitTimeoutMs ({maxWaitTimeoutMs})");
        }
        return new ToolDefinition(
            Name: "job_output",
            Description: "Read a background job. Stream jobs return only output since the previous read; "
                + "final-output jobs return their result after settlement. Every response ends with "
                + "`[status: ...]`. Reads are non-blocking unless `wait: true`, which waits up to the configured cap.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(JobOutputParametersJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(JobOutputOutputSchemaJson)!),
            Execute: async (args, context) =>
            {
                var id = ValidateJobId(args);
                if (TryGetWait(args) is true)
                {
                    var timeout = Math.Min(TryGetTimeoutMs(args) ?? waitTimeoutMs, maxWaitTimeoutMs);
                    await jobs.WaitAsync(id, timeout, context.Session?.Id.Value).ConfigureAwait(false);
                }
                var read = jobs.Read(id, context.Session?.Id.Value);
                return JsonSerializer.SerializeToElement(new { text = read.Text, job = PublicJob(read.Snapshot) });
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderJobOutput(value)) },
            PersistMeta: false);
    }

    /// <summary>The <c>job_list</c> ToolDefinition over the mounted jobs service.</summary>
    public static ToolDefinition JobListDefinition(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var jobs = ctx.Get<IJobsService>("jobs")
            ?? throw new InvalidOperationException("job_list: the \"jobs\" service is not mounted");
        return new ToolDefinition(
            Name: "job_list",
            Description: "List your background jobs (running and finished) with their ids, kinds, and statuses.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse("{}")!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(JobListOutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var snapshots = jobs.List(context.Session?.Id.Value).Select(PublicJob).ToArray();
                return Task.FromResult(JsonSerializer.SerializeToElement(snapshots));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderJobList(value)) },
            PersistMeta: false);
    }

    /// <summary>The <c>job_kill</c> ToolDefinition over the mounted jobs service.</summary>
    public static ToolDefinition JobKillDefinition(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var jobs = ctx.Get<IJobsService>("jobs")
            ?? throw new InvalidOperationException("job_kill: the \"jobs\" service is not mounted");
        return new ToolDefinition(
            Name: "job_kill",
            Description: "Request cancellation of a running background job by job id. Returns immediately; the job settles as killed once its work actually stops.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(JobKillParametersJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(JobKillOutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var id = ValidateJobId(args);
                var reason = args.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : null;
                var result = jobs.Kill(id, context.Session?.Id.Value, reason);
                // A snapshot describes current state without consuming pending output.
                var snapshot = PublicJob(jobs.Get(id, context.Session?.Id.Value));
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    outcome = result == JobKillResult.AlreadyFinished ? "already-finished" : "cancellation-requested",
                    job = snapshot,
                }));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderJobKill(value)) },
            PersistMeta: false);
    }

    /// <summary>Validate the non-empty constraint that the parameter schema cannot express.</summary>
    public static JobId ValidateJobId(JsonElement args)
    {
        var value = args.TryGetProperty("job_id", out var element) ? element.GetString() : null;
        if (value is null || value.Length == 0)
        {
            throw new ArgumentException($"invalid job_id: expected a non-empty string, got {JsonSerializer.Serialize(value)}");
        }
        return new JobId(value);
    }

    private static bool? TryGetWait(JsonElement args)
        => args.TryGetProperty("wait", out var element) && element.ValueKind == JsonValueKind.True;

    private static int? TryGetTimeoutMs(JsonElement args)
        => args.TryGetProperty("timeout_ms", out var element) && element.TryGetInt32(out var timeout)
            ? timeout
            : null;

    private static string RenderJobOutput(JsonElement value)
    {
        var text = value.GetProperty("text").GetString() ?? string.Empty;
        var body = text.Length > 0 ? text : "(no new output)";
        var job = value.GetProperty("job");
        var separator = body.EndsWith('\n') ? string.Empty : "\n";
        return body + separator + StatusLine(StatusOf(job), DetailOf(job));
    }

    private static string RenderJobList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            return "(no background jobs)";
        }
        return string.Join('\n', value.EnumerateArray().Select(job =>
            $"{job.GetProperty("id").GetString()} [{job.GetProperty("kind").GetString()}] {JobStatuses.WireName(StatusOf(job))} — {job.GetProperty("label").GetString()}"));
    }

    private static string RenderJobKill(JsonElement value)
    {
        var job = value.GetProperty("job");
        var outcome = value.GetProperty("outcome").GetString();
        return outcome == "already-finished"
            ? $"job {job.GetProperty("id").GetString()} had already finished {StatusLine(StatusOf(job), DetailOf(job))}"
            : $"requested cancellation of job {job.GetProperty("id").GetString()}";
    }

    private static JobStatus StatusOf(JsonElement job)
    {
        var name = job.GetProperty("status").GetString() ?? string.Empty;
        return name switch
        {
            "running" => JobStatus.Running,
            "stopping" => JobStatus.Stopping,
            "completed" => JobStatus.Completed,
            "killed" => JobStatus.Killed,
            "failed" => JobStatus.Failed,
            _ => throw new ArgumentException($"invalid job status \"{name}\""),
        };
    }

    private static string? DetailOf(JsonElement job)
        => job.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
}
