using System.Text.Json.Serialization;
using Harness.Llm;
using Harness.Session;

namespace Harness.Workflow;

/// <summary>Identifies one workflow run. The engine mints UUIDs; tests may pass fixtures.</summary>
[JsonConverter(typeof(StringIdJsonConverter<WorkflowRunId>))]
public readonly record struct WorkflowRunId(string Value) : IStringId
{
    public static implicit operator string(WorkflowRunId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>
/// One phase declared in a workflow definition's <c>meta.phases</c> (progress vocabulary only —
/// phases group steps in observers; they impose no execution structure).
/// </summary>
public sealed record WorkflowPhase(
    /// <summary>The phase title; <see cref="WorkflowStepContext.Phase"/> calls match against it by exact string.</summary>
    string Title,
    /// <summary>Optional one-line description of what the phase does.</summary>
    string? Detail = null,
    /// <summary>Optional provider override this phase is expected to use (informational).</summary>
    string? Provider = null,
    /// <summary>Optional model override this phase is expected to use (informational).</summary>
    string? Model = null);

/// <summary>The workflow's identity block. <c>name</c>/<c>description</c> are required; the rest is optional annotation.</summary>
public sealed record WorkflowMeta(
    /// <summary>Short kebab-case workflow name (display + registry key).</summary>
    string Name,
    /// <summary>One-line description of what the workflow does.</summary>
    string Description,
    /// <summary>Optional guidance on when this workflow applies (shown in listings).</summary>
    string? WhenToUse = null,
    /// <summary>Optional phase declarations matched by <c>phase()</c> calls.</summary>
    IReadOnlyList<WorkflowPhase>? Phases = null);

/// <summary>Why a run settled. CLOSED union: completed = all steps ran; cancelled = the run was cancelled; error = a step threw.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowStopReason
{
    /// <summary>The run executed every step to its final result.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,
    /// <summary>The run was cancelled (caller <c>Cancel()</c> or the start signal).</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    /// <summary>A step threw or the result failed materialization.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>The TS wire string for one stop reason.</summary>
public static class WorkflowStopReasons
{
    /// <summary>The wire string for one stop reason.</summary>
    public static string WireName(WorkflowStopReason reason) => reason switch
    {
        WorkflowStopReason.Completed => "completed",
        WorkflowStopReason.Cancelled => "cancelled",
        WorkflowStopReason.Error => "error",
        _ => reason.ToString(),
    };
}

/// <summary>
/// The outcome resolved by a live workflow run. <c>Value</c> is the final step's materialized
/// value — meaningful only for <see cref="WorkflowStopReason.Completed"/>. A non-completed reason
/// carries the failure in <c>Error</c>; the consumer maps it to an error tool result rather than
/// reporting partial output. Port of the TS <c>WorkflowResult</c>: <c>StepsStarted</c> replaces the
/// TS <c>agentsStarted</c> because the C# spine has no subagent seam — steps are the units the run
/// starts.
/// </summary>
public sealed record WorkflowResult(
    /// <summary>The final step's return value (host JSON data; null for a valueless step).</summary>
    object? Value,
    /// <summary>Why the run settled.</summary>
    WorkflowStopReason StopReason,
    /// <summary>The failure message (present iff <c>StopReason</c> is not completed).</summary>
    string? Error = null,
    /// <summary>How many steps the run started over its whole lifetime.</summary>
    int StepsStarted = 0);

/// <summary>A settled run's outcome as event data (the <c>workflow/end</c> payload) — the result minus its value.</summary>
public sealed record WorkflowResultInfo(
    /// <summary>Why the run settled.</summary>
    WorkflowStopReason StopReason,
    /// <summary>The failure message (present iff <c>StopReason</c> is not completed).</summary>
    string? Error = null,
    /// <summary>How many steps the run started.</summary>
    int StepsStarted = 0);

/// <summary>Identifying detail for a run, carried by every <c>workflow/*</c> event.</summary>
public sealed record WorkflowRunInfo(WorkflowRunId Id, WorkflowMeta Meta);

/// <summary>A read-only projection of one run, safe to hand to observers and tools.</summary>
public sealed record WorkflowRunSnapshot(
    /// <summary>The run's id.</summary>
    WorkflowRunId Id,
    /// <summary>The run's meta block.</summary>
    WorkflowMeta Meta,
    /// <summary>Whether the run has settled (its result resolved).</summary>
    bool Settled,
    /// <summary>Why the run settled; null while live.</summary>
    WorkflowStopReason? StopReason,
    /// <summary>The failure message; null while live or on a completed run.</summary>
    string? Error,
    /// <summary>How many steps the run started.</summary>
    int StepsStarted,
    /// <summary>Epoch ms when the run started.</summary>
    long StartedAt,
    /// <summary>Epoch ms when the run settled; null while live.</summary>
    long? FinishedAt);

/// <summary>
/// The context one workflow step receives: the run's <c>args</c> value, phase/log narration
/// routed to the <c>workflow/*</c> events, and the run's cancellation token.
/// </summary>
public sealed class WorkflowStepContext
{
    private readonly Action<string> _phase;
    private readonly Action<string> _log;

    internal WorkflowStepContext(object? args, Action<string> phase, Action<string> log, CancellationToken cancellationToken)
    {
        Args = args;
        _phase = phase ?? throw new ArgumentNullException(nameof(phase));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        CancellationToken = cancellationToken;
    }

    /// <summary>The run's <c>args</c> input, verbatim (plain JSON data).</summary>
    public object? Args { get; }

    /// <summary>The run's cancellation token; steps observe it cooperatively.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Start a progress phase (the <c>phase(title)</c> hook); no execution semantics.</summary>
    public void Phase(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        _phase(title);
    }

    /// <summary>Narrate a progress line (the <c>log(message)</c> hook).</summary>
    public void Log(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _log(message);
    }
}

/// <summary>One step body of a workflow definition. Runs on a thread-pool worker task; observe cancellation through the context token.</summary>
public delegate Task<object?> WorkflowStep(WorkflowStepContext context, CancellationToken cancellationToken);

/// <summary>
/// A registered workflow: its validated meta block plus the ordered steps the provider executes.
/// The TS seam carries a model-written script body; the C# port replaces the script with
/// host-registered C# step delegates (see <see cref="IWorkflowService.Register"/>).
/// </summary>
public sealed record WorkflowDefinition(WorkflowMeta Meta, IReadOnlyList<WorkflowStep> Steps);

/// <summary>What a caller asks for when starting a workflow run.</summary>
public sealed record WorkflowRunStartRequest(
    /// <summary>The registered definition name to run.</summary>
    string DefinitionName,
    /// <summary>Optional input exposed verbatim to the steps as <c>WorkflowStepContext.Args</c>.</summary>
    object? Args = null,
    /// <summary>Optional owning session id, recorded for correlation.</summary>
    string? ParentSession = null,
    /// <summary>Cancels the run when cancelled.</summary>
    CancellationToken CancellationToken = default);

/// <summary>Machine-routable fatal workflow failure codes (subset of the TS taxonomy used by this port).</summary>
public static class WorkflowErrorCode
{
    /// <summary>A malformed meta block.</summary>
    public const string MetaInvalid = "META_INVALID";

    /// <summary>An invalid start request (unknown definition, malformed argument).</summary>
    public const string InvalidArgument = "INVALID_ARGUMENT";

    /// <summary>The run was cancelled (reported through the result, not thrown).</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>Typed error for workflow-seam failures; <see cref="Code"/> is machine-routable.</summary>
public sealed class WorkflowError : Exception
{
    /// <summary>Create the error with its machine-routable <paramref name="code"/>.</summary>
    public WorkflowError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The machine-routable failure taxonomy code.</summary>
    public string Code { get; }
}

/// <summary>Opens one durable top-level workflow run record (the TS <c>tool-workflow/run-start</c>).</summary>
public sealed record ToolWorkflowRunStartEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "tool-workflow/run-start";

    /// <summary>The run's id.</summary>
    public required string RunId { get; init; }

    /// <summary>The workflow's display name.</summary>
    public required string Name { get; init; }

    /// <inheritdoc />
    public override string Type => EventTypeName;
}

/// <summary>Closes one workflow run record after cleanup (the TS <c>tool-workflow/run-end</c>).</summary>
public sealed record ToolWorkflowRunEndEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "tool-workflow/run-end";

    /// <summary>The run's id.</summary>
    public required string RunId { get; init; }

    /// <summary>The terminal stop reason.</summary>
    public required WorkflowStopReason StopReason { get; init; }

    /// <inheritdoc />
    public override string Type => EventTypeName;
}
