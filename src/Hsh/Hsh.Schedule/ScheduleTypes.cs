namespace Harness.Schedule;

/// <summary>Stable identity of one registered schedule task; unique and never reused within one provider.</summary>
public readonly record struct ScheduleTaskId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Whether a task fires once or on a fixed period.</summary>
public enum ScheduleKind
{
    /// <summary>Fires exactly once after its delay, then leaves the list.</summary>
    Once,
    /// <summary>Fires on a fixed period until cancelled.</summary>
    Recurring,
}

/// <summary>Read-only runtime projection of one registered schedule task.</summary>
public sealed record ScheduleTask(ScheduleTaskId Id, string Name, ScheduleKind Kind, long DelayMs, int Fired);
