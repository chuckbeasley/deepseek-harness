using Harness.Cordis.Core;
using NativeTimer = System.Threading.Timer;

namespace Harness.Cordis.Plugin.Timer;

/// <summary>Minimal <see cref="IDisposable"/> wrapping one action; used as an effect cleanup.</summary>
internal sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}

/// <summary>
/// One-shot scheduling handle for <c>ctx.timeout()</c>. Fires exactly once; the callback runs
/// while the gate is held, so <see cref="Dispose"/> waits for an in-flight callback (teardown
/// drains) and no callback invocation starts after it returns. Disposal before the due time
/// cancels the pending callback; a reentrant <see cref="Dispose"/> from inside the callback is
/// safe (the gate is reentrant on the calling thread).
/// </summary>
internal sealed class OneShotTimer : IDisposable
{
    private readonly object _gate = new();
    private readonly long _delayMs;
    private readonly Action _callback;
    private readonly Context _ctx;
    private NativeTimer? _timer;
    private bool _disposed;

    public OneShotTimer(long delayMs, Action callback, Context ctx)
    {
        _delayMs = delayMs;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Arm the one-shot timer; a no-op when already disposed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer = new NativeTimer(OnTick, null, _delayMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            try
            {
                _callback();
            }
            catch (Exception error)
            {
                // Node would crash the process on an uncaught callback exception; the port
                // contains the failure in the context logger (the timer is already spent).
                _ctx.Logger.Error(error);
            }
        }
    }
}

/// <summary>
/// Periodic scheduling handle for <c>ctx.interval()</c>. The timer is re-armed one-shot after
/// each callback, so ticks never overlap and a callback that outlives its period delays the next
/// tick by the overrun (Node's single-threaded intervals fire as soon as the loop frees and never
/// catch up). Callbacks run while the gate is held, giving disposal the same drain guarantee as
/// <see cref="OneShotTimer"/>; an uncaught callback exception is logged and stops the interval.
/// </summary>
internal sealed class RepeatingTimer : IDisposable
{
    private readonly object _gate = new();
    private readonly long _delayMs;
    private readonly Action _callback;
    private readonly Context _ctx;
    private NativeTimer? _timer;
    private long _dueAt;
    private bool _disposed;

    public RepeatingTimer(long delayMs, Action callback, Context ctx)
    {
        _delayMs = delayMs;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Start the periodic schedule; a no-op when already disposed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _dueAt = Environment.TickCount64 + _delayMs;
            _timer = new NativeTimer(OnTick, null, _delayMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _callback();
            }
            catch (Exception error)
            {
                // Node would crash the process; the port logs the failure and stops the interval.
                _ctx.Logger.Error(error);
                Dispose();
                return;
            }
            var overrun = Environment.TickCount64 - _dueAt;
            var next = Math.Max(0L, _delayMs - overrun);
            _dueAt = Environment.TickCount64 + next;
            _timer!.Change(next, Timeout.Infinite);
        }
    }
}

/// <summary>
/// Throttled wrapper returned by <c>ctx.throttle()</c> (port of the TS throttle with its
/// <c>dispose</c> property). <see cref="Invoke"/> runs the callback immediately when the delay
/// since the last call has elapsed, otherwise schedules a trailing call — suppressed when
/// noTrailing was set at construction or after disposal. Calls outside the
/// window still run after disposal, mirroring the TS trigger's immediate branch. The TS variadic
/// argument forwarding is dropped on the C# surface (callbacks take no arguments).
/// </summary>
public sealed class ThrottledAction : IDisposable
{
    private readonly object _gate = new();
    private readonly long _delayMs;
    private readonly Action _callback;
    private readonly LoggerService _logger;
    private readonly bool _noTrailing;
    private NativeTimer? _pending;
    private long? _lastCall; // null until the first execution (TS -Infinity)
    private bool _disposed;

    internal ThrottledAction(Action callback, long delayMs, LoggerService logger, bool noTrailing)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _delayMs = delayMs;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _noTrailing = noTrailing;
    }

    /// <summary>
    /// Invoke the throttled call: run immediately when the window has elapsed since the last
    /// call, otherwise schedule (or reschedule) a trailing call.
    /// </summary>
    public void Invoke()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var remaining = _lastCall is null ? long.MinValue : _delayMs - now + _lastCall.Value;
            if (remaining <= 0)
            {
                _lastCall = now;
                RunCallback();
            }
            else if (!_noTrailing && !_disposed)
            {
                _pending?.Dispose();
                _pending = new NativeTimer(OnTrailing, null, remaining, Timeout.Infinite);
            }
        }
    }

    private void OnTrailing(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = null;
            _lastCall = Environment.TickCount64;
            RunCallback();
        }
    }

    private void RunCallback()
    {
        try
        {
            _callback();
        }
        catch (Exception error)
        {
            _logger.Error(error);
        }
    }

    /// <summary>Cancel a pending trailing call; further calls outside the window still run immediately.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Dispose();
            _pending = null;
        }
    }
}

/// <summary>
/// Debounced wrapper returned by <c>ctx.debounce()</c> (port of the TS debounce with its
/// <c>dispose</c> property). Each <see cref="Invoke"/> resets the quiet period and the callback
/// runs once after the last call. Disposal cancels the pending call and suppresses further calls.
/// </summary>
public sealed class DebouncedAction : IDisposable
{
    private readonly object _gate = new();
    private readonly long _delayMs;
    private readonly Action _callback;
    private readonly LoggerService _logger;
    private NativeTimer? _pending;
    private bool _disposed;

    internal DebouncedAction(Action callback, long delayMs, LoggerService logger)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _delayMs = delayMs;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Schedule the call after the quiet period, cancelling any previously pending call.</summary>
    public void Invoke()
    {
        lock (_gate)
        {
            if (_disposed) return; // the TS cleanup suppresses calls after disposal
            _pending?.Dispose();
            _pending = new NativeTimer(OnPending, null, _delayMs, Timeout.Infinite);
        }
    }

    private void OnPending(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = null;
            RunCallback();
        }
    }

    private void RunCallback()
    {
        try
        {
            _callback();
        }
        catch (Exception error)
        {
            _logger.Error(error);
        }
    }

    /// <summary>Cancel the pending call; further calls are no-ops.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Dispose();
            _pending = null;
        }
    }
}

/// <summary>
/// Async-iterator form of <c>ctx.interval()</c> (port of the TS <c>AsyncIterableIterator</c>
/// overload). The effect is registered at construction and lives until the fiber unloads or the
/// enumerator is disposed; the iterator is single-shot. A pending <c>MoveNextAsync</c> faults
/// with <see cref="InvalidOperationException"/> carrying <c>"Context has been disposed"</c> when
/// the fiber unloads first, mirroring the TS rejection; disposing the enumerator ends the
/// iteration gracefully, mirroring the TS <c>return()</c>.
/// </summary>
internal sealed class TimerIntervalEnumerable : IAsyncEnumerable<int>
{
    private readonly object _gate = new();
    private readonly IDisposable _effect;
    private readonly long _delayMs;
    private TaskCompletionSource<int>? _waiter;
    private NativeTimer? _timer;
    private long _dueAt;
    private int _tick;
    private bool _ended;
    private bool _throwing;

    /// <summary>Lock guarding every mutable field; also serializes tick handling vs. teardown.</summary>
    internal object Gate => _gate;

    /// <summary>The waiter of the current <c>MoveNextAsync</c>, or <c>null</c>.</summary>
    internal TaskCompletionSource<int>? Waiter
    {
        get => _waiter;
        set => _waiter = value;
    }

    /// <summary>True once the iterator ended gracefully (enumerator disposal).</summary>
    internal bool Ended => _ended;

    /// <summary>True once the fiber unloaded while the iterator was live (pending waiters fault).</summary>
    internal bool Throwing => _throwing;

    public TimerIntervalEnumerable(Context ctx, long delayMs)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _delayMs = delayMs;
        _effect = ctx.Effect(() =>
        {
            lock (_gate)
            {
                _dueAt = Environment.TickCount64 + delayMs;
                _timer = new NativeTimer(OnTick, null, delayMs, Timeout.Infinite);
            }
            return new ActionDisposer(DisposeCore);
        }, "ctx.interval()");
    }

    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state => ((TimerIntervalEnumerable)state!).EndIteration(), this);
        }
        return new TimerIntervalEnumerator(this);
    }

    private void OnTick(object? state)
    {
        TaskCompletionSource<int>? waiter;
        int value;
        lock (_gate)
        {
            if (_ended || _throwing) return;
            waiter = _waiter;
            _waiter = null;
            value = ++_tick;
            var overrun = Environment.TickCount64 - _dueAt;
            var next = Math.Max(0L, _delayMs - overrun);
            _dueAt = Environment.TickCount64 + next;
            _timer!.Change(next, Timeout.Infinite);
        }
        waiter?.TrySetResult(value);
    }

    /// <summary>
    /// Fiber-teardown cleanup: cancel the timer and fault a pending waiter (the TS 'throw'
    /// path). When the iterator already ended gracefully the waiter is resolved instead.
    /// </summary>
    private void DisposeCore()
    {
        TaskCompletionSource<int>? waiter;
        bool throwIt;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            waiter = _waiter;
            _waiter = null;
            throwIt = !_ended;
            if (throwIt)
            {
                _throwing = true;
                _ended = true;
            }
        }
        if (waiter is null) return;
        if (throwIt) waiter.TrySetException(new InvalidOperationException("Context has been disposed"));
        else waiter.TrySetResult(-1);
    }

    /// <summary>
    /// End the iteration gracefully (the TS <c>return()</c> path): wake a pending waiter as
    /// completed, then dispose the effect so the fiber teardown does not fault it.
    /// </summary>
    internal void EndIteration()
    {
        TaskCompletionSource<int>? waiter;
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            waiter = _waiter;
            _waiter = null;
        }
        _effect.Dispose();
        waiter?.TrySetResult(-1);
    }
}

/// <summary>Enumerator returned by <see cref="TimerIntervalEnumerable.GetAsyncEnumerator"/>.</summary>
internal sealed class TimerIntervalEnumerator : IAsyncEnumerator<int>
{
    private readonly TimerIntervalEnumerable _owner;

    public TimerIntervalEnumerator(TimerIntervalEnumerable owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public int Current { get; private set; }

    public ValueTask<bool> MoveNextAsync()
    {
        TaskCompletionSource<int>? waiter;
        lock (_owner.Gate)
        {
            if (_owner.Throwing)
            {
                return ValueTask.FromException<bool>(new InvalidOperationException("Context has been disposed"));
            }
            if (_owner.Ended) return ValueTask.FromResult(false);
            waiter = _owner.Waiter = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return AwaitTick(waiter);
    }

    private async ValueTask<bool> AwaitTick(TaskCompletionSource<int> waiter)
    {
        var value = await waiter.Task;
        Current = value;
        return value > 0;
    }

    public ValueTask DisposeAsync()
    {
        _owner.EndIteration();
        return ValueTask.CompletedTask;
    }
}
