using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Jobs;

/// <summary>Identifies one background job. The registry mints `&lt;kind&gt;-N`; predictable ids rely on owner authorization, not secrecy.</summary>
[JsonConverter(typeof(StringIdJsonConverter<JobId>))]
public readonly record struct JobId(string Value) : IStringId
{
    public static implicit operator string(JobId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Task lifecycle: running, optionally stopping, then exactly one terminal status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    /// <summary>Work is in flight.</summary>
    [JsonStringEnumMemberName("running")]
    Running,
    /// <summary>A cancellation request is pending; the producer is still releasing resources.</summary>
    [JsonStringEnumMemberName("stopping")]
    Stopping,
    /// <summary>The job finished normally.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,
    /// <summary>The job was cancelled.</summary>
    [JsonStringEnumMemberName("killed")]
    Killed,
    /// <summary>The job broke (producer failure or contract violation).</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>True for the three terminal <see cref="JobStatus"/> values.</summary>
public static class JobStatuses
{
    /// <summary>Whether the status is terminal (completed, killed, or failed).</summary>
    public static bool IsTerminal(JobStatus status)
        => status is JobStatus.Completed or JobStatus.Killed or JobStatus.Failed;

    /// <summary>The TS wire string for one status.</summary>
    public static string WireName(JobStatus status) => status switch
    {
        JobStatus.Running => "running",
        JobStatus.Stopping => "stopping",
        JobStatus.Completed => "completed",
        JobStatus.Killed => "killed",
        JobStatus.Failed => "failed",
        _ => status.ToString(),
    };
}

/// <summary>Terminal result supplied by a producer through <see cref="JobHooks.Done"/>.</summary>
public sealed record JobOutcome(
    /// <summary>How the job ended: finished (completed), cancelled (killed), or broke (failed).</summary>
    JobStatus Status,
    /// <summary>Kind-specific detail rendered into status lines ('exit code: 3', 'max-tokens').</summary>
    string? Detail = null,
    /// <summary>Final output for jobs without <c>readOutput</c>; stream jobs leave it unset.</summary>
    string? Output = null);

/// <summary>Hooks through which the runtime controls and observes producer work.</summary>
public sealed record JobHooks(
    /// <summary>Request termination. Must be synchronous, idempotent, and eventually settle <see cref="Done"/>; a throw propagates without changing job state.</summary>
    Action<string?> Cancel,
    /// <summary>Resolves after the producer releases its resources. Must not reject; a rejection settles the job failed.</summary>
    Task<JobOutcome> Done,
    /// <summary>Consume output produced since the previous call. Absence marks a final-output-only job; each job has one consuming cursor.</summary>
    Func<string>? ReadOutput = null);

/// <summary>Producer declaration passed to <see cref="IJobsService.Start"/>.</summary>
public sealed record JobStartRequest(
    /// <summary>Producer kind — also the id prefix (e.g. "bash", "test").</summary>
    string Kind,
    /// <summary>One-line model-facing label (the command; the delegation description).</summary>
    string Label,
    /// <summary>Start the work after preflight and synchronously return its hooks. Called once; a throw leaves nothing registered.</summary>
    Func<JobHooks> Run,
    /// <summary>Optional byte cap for each complete model-facing completion notice or output read.</summary>
    int? OutputLimitBytes = null,
    /// <summary>Owning session id used for authorization and correlation; null for unowned work open to any caller.</summary>
    string? OwnerSession = null);

/// <summary>A read-only projection of one job — a fresh object per call, never live registry state.</summary>
public sealed record JobSnapshot(
    /// <summary>The registry-issued id (<c>&lt;kind&gt;-N</c>).</summary>
    JobId Id,
    /// <summary>The producer kind the job was registered with.</summary>
    string Kind,
    /// <summary>The producer-supplied one-line label.</summary>
    string Label,
    /// <summary>Producer-owned cap for complete model-facing notices and output reads.</summary>
    int? OutputLimitBytes,
    /// <summary>Owner session id used for authorization; absent for unowned jobs.</summary>
    string? OwnerSession,
    /// <summary>Current lifecycle state.</summary>
    JobStatus Status,
    /// <summary>Kind-specific status detail, present once the producer supplied one (usually terminal).</summary>
    string? Detail,
    /// <summary>Epoch ms when the job was registered.</summary>
    long StartedAt,
    /// <summary>Epoch ms when the job settled; absent while running/stopping.</summary>
    long? FinishedAt,
    /// <summary>True when a kill, read, wait, or teardown cancel has reported or committed to report the terminal state.</summary>
    bool Reported);

/// <summary>Output and post-read state returned by <see cref="IJobsService.Read"/>.</summary>
public sealed record JobRead(string Text, JobSnapshot Snapshot);

/// <summary>The outcome of <see cref="IJobsService.Kill"/>.</summary>
public enum JobKillResult
{
    /// <summary>Cancellation was requested for live work.</summary>
    Requested,
    /// <summary>The job had already reached a terminal status.</summary>
    AlreadyFinished,
}

/// <summary>Task state safe for model-authored programs; ownership and bookkeeping fields are omitted.</summary>
public sealed record PublicJobSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("status")] JobStatus Status,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("startedAt")] long StartedAt = 0,
    [property: JsonPropertyName("finishedAt")] long? FinishedAt = null);
