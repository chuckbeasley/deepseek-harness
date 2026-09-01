using System.Text.Json.Serialization;
using Harness.Session;

namespace Harness.Goal;

/// <summary>Durable lifecycle phase of one goal. Serialized with the TS wire strings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoalPhase
{
    /// <summary>The goal is open for continuation.</summary>
    [JsonStringEnumMemberName("active")] Active,
    /// <summary>Continuation is stopped; a later resume reopens it.</summary>
    [JsonStringEnumMemberName("paused")] Paused,
    /// <summary>Continuation stopped on a concrete blocking condition.</summary>
    [JsonStringEnumMemberName("blocked")] Blocked,
    /// <summary>The objective is achieved; the goal may be replaced.</summary>
    [JsonStringEnumMemberName("complete")] Complete,
}

/// <summary>Process-local continuation eligibility of the current goal; never persisted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoalActivation
{
    /// <summary>This process may automatically continue the active goal.</summary>
    [JsonStringEnumMemberName("armed")] Armed,
    /// <summary>Continuation requires a fresh human-authorized resume.</summary>
    [JsonStringEnumMemberName("disarmed")] Disarmed,
}

/// <summary>Goal state-changing verbs recorded in the durable goal/write event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoalOperation
{
    /// <summary>A fresh revision-one active goal replaces a completed goal or the empty log.</summary>
    [JsonStringEnumMemberName("create")] Create,
    /// <summary>Objective and/or round cap replaced without changing phase.</summary>
    [JsonStringEnumMemberName("edit")] Edit,
    /// <summary>An active goal stopped without changing its definition.</summary>
    [JsonStringEnumMemberName("pause")] Pause,
    /// <summary>A stopped goal reopened (and armed).</summary>
    [JsonStringEnumMemberName("resume")] Resume,
    /// <summary>A non-complete goal marked achieved.</summary>
    [JsonStringEnumMemberName("complete")] Complete,
    /// <summary>An active goal marked blocked with a durable reason.</summary>
    [JsonStringEnumMemberName("block")] Block,
    /// <summary>The current goal removed, retaining a durable tombstone.</summary>
    [JsonStringEnumMemberName("clear")] Clear,
}

/// <summary>Compare-and-set identity for one exact goal revision.</summary>
public sealed record GoalRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>Machine-routable and human-readable explanation for a blocked goal.</summary>
public sealed record GoalBlockReason(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Full durable state written by every non-clear goal mutation.</summary>
public sealed record GoalSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("phase")] GoalPhase Phase,
    [property: JsonPropertyName("blockedReason")] GoalBlockReason? BlockedReason,
    [property: JsonPropertyName("maxGoalRounds")] int MaxGoalRounds);

/// <summary>
/// The folded current goal state: the goal snapshot of the latest <c>goal/write</c> event
/// (last-write-wins), <c>null</c> before the first create and after a clear. Derives from the
/// session log alone, so resume and fork restore it. Activation is process-local and absent.
/// </summary>
public sealed record GoalState(GoalSnapshot? Goal, int RoundsStarted, long CreatedAt, long UpdatedAt)
{
    /// <summary>The state of a log with no current goal: no snapshot and zero counters.</summary>
    public static GoalState Empty { get; } = new(null, 0, 0, 0);
}

/// <summary>Current goal projection including values derived from the session log and the process-local activation.</summary>
public sealed record GoalView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("phase")] GoalPhase Phase,
    [property: JsonPropertyName("blockedReason")] GoalBlockReason? BlockedReason,
    [property: JsonPropertyName("maxGoalRounds")] int MaxGoalRounds,
    [property: JsonPropertyName("roundsStarted")] int RoundsStarted,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("activation")] GoalActivation Activation);

/// <summary>
/// Plugin-merged session event: full-snapshot goal mutation (last write wins on replay; log-only
/// state, never derived history). A clear carries the tombstone <see cref="Cleared"/> instead of a
/// snapshot. Registered into the session event-type registry at the goal service's construction.
/// The TS goal-round driver (loop-driven continuation rounds advancing <c>roundsStarted</c> from
/// admitted <c>user/message</c> events) is deferred to a later phase: the C# fold is a plain
/// whole-value last-write-wins fold, so the counters travel on the event rather than being replayed
/// from message sources.
/// </summary>
public sealed record GoalWriteEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "goal/write";

    /// <summary>Which state-changing verb committed this mutation.</summary>
    public required GoalOperation Operation { get; init; }

    /// <summary>The complete post-mutation snapshot; absent for a clear.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoalSnapshot? Goal { get; init; }

    /// <summary>Highest admitted goal-round number carried by this snapshot mutation.</summary>
    public int RoundsStarted { get; init; }

    /// <summary>Epoch milliseconds of the create mutation.</summary>
    public long CreatedAt { get; init; }

    /// <summary>Epoch milliseconds of the latest mutation.</summary>
    public long UpdatedAt { get; init; }

    /// <summary>The tombstone ref (revision one past the cleared snapshot); present only for a clear.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoalRef? Cleared { get; init; }

    /// <summary>Epoch milliseconds of the clear; present only for a clear.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClearedAt { get; init; }

    public override string Type => EventTypeName;
}
