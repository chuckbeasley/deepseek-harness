using System.Text.Json.Serialization;
using Harness.Session;

namespace Harness.Plan;

/// <summary>Lifecycle state of one plan item. Serialized with the TS wire strings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanItemStatus
{
    /// <summary>Not started.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>Being worked on now.</summary>
    [JsonStringEnumMemberName("in_progress")] InProgress,
    /// <summary>Finished.</summary>
    [JsonStringEnumMemberName("completed")] Completed,
}

/// <summary>One step in the agent's plan — the unit of the plan/write whole-plan snapshot.</summary>
public sealed record PlanItem([property: JsonPropertyName("content")] string Content, [property: JsonPropertyName("status")] PlanItemStatus Status);

/// <summary>Canonical counts of one plan.</summary>
public sealed record PlanCounts([property: JsonPropertyName("pending")] int Pending, [property: JsonPropertyName("inProgress")] int InProgress, [property: JsonPropertyName("completed")] int Completed);

/// <summary>The canonical result of one plan_write call: the complete plan plus its counts.</summary>
public sealed record PlanWriteResult([property: JsonPropertyName("plan")] IReadOnlyList<PlanItem> Plan, [property: JsonPropertyName("counts")] PlanCounts Counts);

/// <summary>
/// The folded current plan state: the items of the latest <c>plan/write</c> event (last-write-wins),
/// empty before the first. Derives from the session log alone, so resume and fork restore it.
/// </summary>
public sealed record PlanState(IReadOnlyList<PlanItem> Items)
{
    /// <summary>The state of a log with no <c>plan/write</c> event: an empty plan.</summary>
    public static PlanState Empty { get; } = new(Array.Empty<PlanItem>());
}

/// <summary>
/// Plugin-merged session event: whole-plan snapshot (last write wins on replay; log-only state,
/// never derived history). Registered into the session event-type registry at the plan service's
/// construction.
/// </summary>
public sealed record PlanWriteEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "plan/write";

    /// <summary>The complete replacement plan.</summary>
    public required IReadOnlyList<PlanItem> Plan { get; init; }

    public override string Type => EventTypeName;
}
