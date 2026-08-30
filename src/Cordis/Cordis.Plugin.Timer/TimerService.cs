using Cordis.Core;
namespace Cordis.Plugin.Timer;

/// <summary>
/// Disposal-aware scheduling service registered as <c>ctx.timer</c> (C# port of the vendored
/// <c>cordis-plugin-timer</c>). Every handle is registered as a fiber effect on the owning
/// context, so disposing the returned handle — or unloading the context — cancels pending
/// callbacks; a callback already in flight when a handle is disposed completes (teardown drains)
/// and no pending callback starts after disposal.
///
/// Concurrency model (ported from the Node host timers): callbacks of one timer never run
/// concurrently, a tick due while the previous callback is still running fires as soon as the
/// callback returns (no catch-up bursts), and an uncaught callback exception is contained in the
/// context logger instead of crashing the process.
/// </summary>
public sealed class TimerService : Service
{
    private readonly TimerConfig _config;

    /// <summary>Create the timer service and register it under <c>"timer"</c> (an effect on the current fiber).</summary>
    /// <param name="ctx">the context to register in.</param>
    /// <param name="config">plugin configuration; defaults are applied when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException">when <see cref="TimerConfig.MaxDelayMs"/> is negative.</exception>
    public TimerService(Context ctx, TimerConfig? config = null)
        : base(ctx, "timer")
    {
        _config = config ?? new TimerConfig();
        if (_config.MaxDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "MaxDelayMs must be non-negative");
        }
    }

    /// <summary>Deprecated alias for <see cref="Timeout(Action, long)"/> (TS <c>ctx.setTimeout()</c>).</summary>
    public IDisposable SetTimeout(Action callback, long delayMs) => Timeout(callback, delayMs);

    /// <summary>Deprecated alias for <see cref="Interval(Action, long)"/> (TS <c>ctx.setInterval()</c>).</summary>
    public IDisposable SetInterval(Action callback, long delayMs) => Interval(callback, delayMs);

    /// <summary>
    /// Run <paramref name="callback"/> once after <paramref name="delayMs"/> milliseconds and
    /// return a disposer that cancels the pending call (the <c>clearTimeout</c> equivalent). The
    /// registration is an effect, so context teardown cancels it too.
    /// </summary>
    /// <param name="callback">the callback to run once.</param>
    /// <param name="delayMs">delay in milliseconds, within <see cref="TimerConfig.MaxDelayMs"/>.</param>
    /// <returns>a single-shot disposer; disposing it before the due time cancels the callback.</returns>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="delayMs"/> is negative or above the configured cap.</exception>
    /// <exception cref="Cordis.Core.CordisError">with code <c>INACTIVE_EFFECT</c> on a disposed context.</exception>
    public IDisposable Timeout(Action callback, long delayMs)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateDelay(delayMs, nameof(delayMs));
        return Ctx.Effect(() =>
        {
            var handle = new OneShotTimer(delayMs, callback, Ctx);
            handle.Start();
            return handle;
        }, "ctx.timeout()");
    }

    /// <summary>
    /// Return a task that resolves after <paramref name="delayMs"/> milliseconds (the TS
    /// <c>ctx.timeout(delay)</c> promise overload). The task faults with
    /// <see cref="InvalidOperationException"/> carrying the message
    /// <c>"Context has been disposed"</c> when the context is disposed before the delay elapses.
    /// </summary>
    /// <param name="delayMs">delay in milliseconds, within <see cref="TimerConfig.MaxDelayMs"/>.</param>
    /// <returns>a task completing when the delay elapses or faulting on context disposal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="delayMs"/> is negative or above the configured cap.</exception>
    /// <exception cref="Cordis.Core.CordisError">with code <c>INACTIVE_EFFECT</c> on a disposed context.</exception>
    public Task TimeoutAsync(long delayMs)
    {
        ValidateDelay(delayMs, nameof(delayMs));
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ctx.Effect(() =>
        {
            var handle = new OneShotTimer(delayMs, () => tcs.TrySetResult(), Ctx);
            handle.Start();
            return new ActionDisposer(() =>
            {
                handle.Dispose();
                tcs.TrySetException(new InvalidOperationException("Context has been disposed"));
            });
        }, "ctx.timeout()");
        return tcs.Task;
    }

    /// <summary>
    /// Run <paramref name="callback"/> every <paramref name="delayMs"/> milliseconds and return a
    /// disposer that stops the interval (the <c>clearInterval</c> equivalent). Ticks never
    /// overlap: a callback that outlives its interval delays the next tick by the overrun rather
    /// than running concurrently or catching up.
    /// </summary>
    /// <param name="callback">the callback to run on every tick.</param>
    /// <param name="delayMs">period in milliseconds, within <see cref="TimerConfig.MaxDelayMs"/>.</param>
    /// <returns>a single-shot disposer; disposing it cancels pending ticks.</returns>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="delayMs"/> is negative or above the configured cap.</exception>
    /// <exception cref="Cordis.Core.CordisError">with code <c>INACTIVE_EFFECT</c> on a disposed context.</exception>
    public IDisposable Interval(Action callback, long delayMs)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateDelay(delayMs, nameof(delayMs));
        return Ctx.Effect(() =>
        {
            var handle = new RepeatingTimer(delayMs, callback, Ctx);
            handle.Start();
            return handle;
        }, "ctx.interval()");
    }

    /// <summary>
    /// Return an async iterator yielding the 1-based tick index on every interval tick (the TS
    /// <c>ctx.interval(delay)</c> async-iterator overload; C# cannot yield <c>void</c>, so the
    /// tick counter replaces the TS <c>undefined</c> element). The iterator is single-shot: the
    /// effect is registered immediately, a pending <c>MoveNextAsync</c> faults with
    /// <see cref="InvalidOperationException"/> carrying <c>"Context has been disposed"</c> when
    /// the context unloads first, and disposing the enumerator ends the iteration gracefully.
    /// </summary>
    /// <param name="delayMs">period in milliseconds, within <see cref="TimerConfig.MaxDelayMs"/>.</param>
    /// <returns>an async enumerable of tick indices.</returns>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="delayMs"/> is negative or above the configured cap.</exception>
    /// <exception cref="Cordis.Core.CordisError">with code <c>INACTIVE_EFFECT</c> on a disposed context.</exception>
    public IAsyncEnumerable<int> Interval(long delayMs)
    {
        ValidateDelay(delayMs, nameof(delayMs));
        return new TimerIntervalEnumerable(Ctx, delayMs);
    }

    /// <summary>
    /// Return a throttled wrapper (the TS <c>ctx.throttle()</c> with its <c>dispose</c>
    /// property): the first call runs immediately, calls within the window schedule one trailing
    /// call after the window elapses, and <paramref name="noTrailing"/> (or disposal) suppresses
    /// trailing calls. Calls outside the window still run after disposal, mirroring the TS
    /// trigger. The TS variadic argument forwarding is dropped on the C# surface (callbacks take
    /// no arguments).
    /// </summary>
    /// <param name="callback">the callback to throttle.</param>
    /// <param name="delayMs">throttle window in milliseconds, within <see cref="TimerConfig.MaxDelayMs"/>.</param>
    /// <param name="noTrailing">suppress the trailing call (default false).</param>
    /// <returns>a throttled wrapper whose disposal cancels a pending trailing call.</returns>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="delayMs"/> is negative or above the configured cap.</exception>
    /// <exception cref="Cordis.Core.CordisError">with code <c>INACTIVE_EFFECT</c> on a disposed context.</exception>
    public ThrottledAction Throttle(Action callback, long delayMs, bool noTrailing = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateDelay(delayMs, nameof(delayMs));
        var action = new ThrottledAction(callback, delayMs, Ctx.Logger, noTrailing);
        Ctx.Effect(() => new ActionDisposer(action.Dispose), "ctx.throttle()");
        return action;
    }

    /// <summary>
    /// Return a debounced wrapper (the TS <c>ctx.debounce()</c> with its <c>dispose</c>
    /// property): each call resets the quiet period and the callback runs once after the last
    /// call. Disposal cancels the pending call and suppresses further calls.
    /// </summary>
    /// <param name="callback">the callback to debounce.</param>
    /// <param name="delayMs">quiet period in milliseconds, within <see cref="TimerConfig.MaxDelayMs"/>.</param>
    /// <returns>a debounced wrapper whose disposal cancels the pending call.</returns>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="delayMs"/> is negative or above the configured cap.</exception>
    /// <exception cref="Cordis.Core.CordisError">with code <c>INACTIVE_EFFECT</c> on a disposed context.</exception>
    public DebouncedAction Debounce(Action callback, long delayMs)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateDelay(delayMs, nameof(delayMs));
        var action = new DebouncedAction(callback, delayMs, Ctx.Logger);
        Ctx.Effect(() => new ActionDisposer(action.Dispose), "ctx.debounce()");
        return action;
    }

    private void ValidateDelay(long delayMs, string paramName)
    {
        if (delayMs < 0 || delayMs > _config.MaxDelayMs)
        {
            throw new ArgumentOutOfRangeException(paramName, delayMs,
                $"delay must be between 0 and MaxDelayMs ({_config.MaxDelayMs}) milliseconds");
        }
    }
}
