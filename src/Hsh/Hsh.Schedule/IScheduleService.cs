namespace Harness.Schedule;

/// <summary>
/// The schedule capability surface (ctx.schedule): register recurring and once tasks with stable
/// ids over a timer provider, cancel them, and list the current registrations. The TS schedule
/// package additionally keeps a durable <c>schedule/change</c> log, validates time-zone/rule
/// selectors, and delivers due reminders into the owning session; those surfaces are deferred —
/// this seam ports only the runtime task-registration contract.
/// </summary>
public interface IScheduleService
{
    /// <summary>Register a task that fires once after <paramref name="delay"/> and then leaves the list.</summary>
    /// <param name="name">short reminder/task label (trimmed, non-empty).</param>
    /// <param name="delay">strictly positive delay.</param>
    /// <param name="callback">the work to run when the task fires; a throw is contained and logged.</param>
    /// <returns>the stable task id; use it to cancel or observe the task.</returns>
    ScheduleTaskId RegisterOnce(string name, TimeSpan delay, Action callback);

    /// <summary>Register a task that fires on a fixed period until cancelled.</summary>
    /// <param name="name">short reminder/task label (trimmed, non-empty).</param>
    /// <param name="period">strictly positive period; a throwing callback never stops the schedule.</param>
    /// <param name="callback">the work to run on every tick; a throw is contained and logged.</param>
    /// <returns>the stable task id; use it to cancel or observe the task.</returns>
    ScheduleTaskId RegisterRecurring(string name, TimeSpan period, Action callback);

    /// <summary>Cancel a registered task, preventing any pending or future fire.</summary>
    /// <param name="id">the id returned at registration.</param>
    /// <returns>true when the task was still registered and was cancelled.</returns>
    bool Cancel(ScheduleTaskId id);

    /// <summary>Project the currently registered tasks, in registration order.</summary>
    IReadOnlyList<ScheduleTask> List();
}
