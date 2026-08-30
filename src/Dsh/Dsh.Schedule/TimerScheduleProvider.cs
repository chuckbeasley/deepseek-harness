using Cordis.Core;
using Cordis.Plugin.Timer;

namespace Dsh.Schedule;

/// <summary>
/// ctx.schedule: the timer-backed schedule provider. Every registration is an effect on the
/// owning context (the timer handle), so context teardown cancels pending tasks; the provider's
/// <see cref="StopAsync"/> then drops its bookkeeping. Ticks of one task never overlap (the timer
/// port's guarantee). Task failures are contained and logged at the provider boundary — a
/// throwing callback never reaches the timer port, so a recurring task keeps its schedule and a
/// once task still terminates cleanly.
/// </summary>
public sealed class TimerScheduleProvider : Service, IScheduleService
{
    private readonly TimerService _timer;
    private readonly object _gate = new();
    private readonly Dictionary<ScheduleTaskId, Entry> _tasks = new();
    private int _counter;

    /// <summary>Create the provider and register it as <c>schedule</c>.</summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <exception cref="InvalidOperationException">when the <c>timer</c> service is not mounted.</exception>
    public TimerScheduleProvider(Context ctx)
        : base(ctx, "schedule")
    {
        _timer = ctx.Get<TimerService>("timer")
            ?? throw new InvalidOperationException("schedule requires the \"timer\" service; apply Cordis.Plugin.Timer first");
    }

    /// <inheritdoc />
    public ScheduleTaskId RegisterOnce(string name, TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var ms = ValidateDelay(delay, nameof(delay));
        var id = NewId();
        var entry = new Entry(new ScheduleTask(id, ResolveName(name), ScheduleKind.Once, ms, 0), callback);
        lock (_gate) _tasks[id] = entry;
        try
        {
            entry.Handle = _timer.Timeout(() => FireOnce(entry), ms);
        }
        catch
        {
            lock (_gate) _tasks.Remove(id);
            throw;
        }
        return id;
    }

    /// <inheritdoc />
    public ScheduleTaskId RegisterRecurring(string name, TimeSpan period, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var ms = ValidateDelay(period, nameof(period));
        var id = NewId();
        var entry = new Entry(new ScheduleTask(id, ResolveName(name), ScheduleKind.Recurring, ms, 0), callback);
        lock (_gate) _tasks[id] = entry;
        try
        {
            entry.Handle = _timer.Interval(() => FireRecurring(entry), ms);
        }
        catch
        {
            lock (_gate) _tasks.Remove(id);
            throw;
        }
        return id;
    }

    /// <inheritdoc />
    public bool Cancel(ScheduleTaskId id)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var entry)) return false;
            _tasks.Remove(id);
            entry.Handle?.Dispose();
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduleTask> List()
    {
        lock (_gate) return _tasks.Values.Select(entry => entry.Info).ToArray();
    }

    /// <summary>Drop the bookkeeping; the fiber already cancelled every timer handle (registered after this service).</summary>
    public override ValueTask StopAsync()
    {
        lock (_gate) _tasks.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>One-shot fire: remove the task from the list, count the fire, then run the callback contained.</summary>
    private void FireOnce(Entry entry)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(entry.Info.Id, out var current) || !ReferenceEquals(current, entry)) return;
            _tasks.Remove(entry.Info.Id);
        }
        entry.Info = entry.Info with { Fired = entry.Info.Fired + 1 };
        RunContained(entry.Callback);
    }

    /// <summary>Recurring tick: count the fire and run the callback contained; a failure never stops the schedule.</summary>
    private void FireRecurring(Entry entry)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(entry.Info.Id, out var current) || !ReferenceEquals(current, entry)) return;
            entry.Info = entry.Info with { Fired = entry.Info.Fired + 1 };
        }
        RunContained(entry.Callback);
    }

    /// <summary>Contain one task callback: a throw is logged and the provider keeps working.</summary>
    private void RunContained(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception error)
        {
            // The timer port would otherwise stop a recurring interval on the first throw; the
            // provider contains the failure instead so the task schedule survives (TS due
            // handling rechecks the wall clock on the next tick rather than dying).
            Ctx.Logger.Error(error);
        }
    }

    private ScheduleTaskId NewId()
    {
        lock (_gate) return new ScheduleTaskId($"schedule-{++_counter}");
    }

    private static string ResolveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("schedule task name must be a non-empty string", nameof(name));
        }
        return trimmed;
    }

    private static long ValidateDelay(TimeSpan delay, string paramName)
    {
        var ms = (long)delay.TotalMilliseconds;
        if (delay <= TimeSpan.Zero || ms <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, delay, "delay must be strictly positive");
        }
        return ms;
    }

    /// <summary>One registered task: its read-only projection plus the live callback and timer handle.</summary>
    private sealed class Entry
    {
        public Entry(ScheduleTask info, Action callback)
        {
            Info = info;
            Callback = callback;
        }

        public ScheduleTask Info { get; set; }

        public Action Callback { get; }

        public IDisposable? Handle { get; set; }
    }
}
