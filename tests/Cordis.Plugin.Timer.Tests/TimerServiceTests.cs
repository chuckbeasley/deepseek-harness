using Cordis.Core;
using Cordis.Plugin.Timer;

namespace Cordis.Plugin.Timer.Tests;

/// <summary>
/// Behavioral tests for the timer plugin port. Timing assertions use short real delays with
/// generous polling margins; the cancellation and teardown tests dispose well before the due time
/// and then wait, so they do not depend on timer precision.
/// </summary>
public static class TimerServiceTests
{
    /// <summary>Applying the plugin registers the service at <c>"timer"</c>; applying again is a no-op.</summary>
    public static async Task Apply_RegistersServiceUnderTimerKey_AndIsIdempotent()
    {
        var ctx = new Context();
        try
        {
            var service = TimerPlugin.Apply(ctx);
            Assert.NotNull(ctx.Get<TimerService>("timer"));
            Assert.True(ReferenceEquals(service, TimerPlugin.Apply(ctx)));
            await ctx.DisposeAsync();
            Assert.Null(ctx.Get<TimerService>("timer")); // the registration effect unwound
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>A timeout fires its callback exactly once, on schedule.</summary>
    public static async Task Timeout_FiresCallbackOnceOnSchedule()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var fired = 0;
            ctx.Timeout(() => Interlocked.Increment(ref fired), 40);
            await WaitUntil(() => fired >= 1);
            Assert.Equal(1, fired, "timeout fired once");
            await Task.Delay(120);
            Assert.Equal(1, fired, "timeout must not fire again");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Disposing the returned registration cancels the pending callback (clearTimeout).</summary>
    public static async Task Timeout_DisposeCancelsPendingCallback()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var fired = 0;
            var registration = ctx.Timeout(() => fired++, 40);
            registration.Dispose();
            await Task.Delay(200);
            Assert.Equal(0, fired, "disposed timeout must not fire");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Disposing the context cancels every pending timer callback; none invokes afterwards.</summary>
    public static async Task TimeoutAndInterval_ContextDispose_CancelsPendingCallbacks_NoPostDisposeInvocation()
    {
        var ctx = new Context();
        TimerPlugin.Apply(ctx);
        var timeouts = 0;
        var intervals = 0;
        ctx.Timeout(() => timeouts++, 30);
        ctx.Interval(() => intervals++, 30);
        await ctx.DisposeAsync();
        await Task.Delay(250);
        Assert.Equal(0, timeouts, "no timeout callback after context disposal");
        Assert.Equal(0, intervals, "no interval callback after context disposal");
    }

    /// <summary>The promise overload resolves after the delay.</summary>
    public static async Task TimeoutAsync_ResolvesAfterDelay()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await ctx.TimeoutAsync(50);
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds >= 40, $"resolved after {watch.ElapsedMilliseconds}ms, expected >= 40ms");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>The promise overload faults with "Context has been disposed" when the context unloads first.</summary>
    public static async Task TimeoutAsync_FaultsWithContextDisposedOnTeardown()
    {
        var ctx = new Context();
        TimerPlugin.Apply(ctx);
        var pending = ctx.TimeoutAsync(120);
        await ctx.DisposeAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
        Assert.Equal("Context has been disposed", error.Message);
    }

    /// <summary>An interval fires repeatedly on schedule until disposed.</summary>
    public static async Task Interval_FiresRepeatedlyOnSchedule_UntilDisposed()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var ticks = 0;
            var registration = ctx.Interval(() => Interlocked.Increment(ref ticks), 30);
            await WaitUntil(() => ticks >= 3);
            Assert.True(ticks >= 3, $"interval produced {ticks} ticks, expected at least 3");
            registration.Dispose();
            var after = ticks;
            await Task.Delay(150);
            Assert.Equal(after, ticks, "interval must stop after disposal");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Context teardown cancels pending interval ticks.</summary>
    public static async Task Interval_ContextDispose_CancelsPendingTicks()
    {
        var ctx = new Context();
        TimerPlugin.Apply(ctx);
        var ticks = 0;
        ctx.Interval(() => Interlocked.Increment(ref ticks), 30);
        await ctx.DisposeAsync();
        await Task.Delay(200);
        Assert.Equal(0, ticks, "no interval ticks after context disposal");
    }

    /// <summary>A callback that outlives its period never runs concurrently with itself.</summary>
    public static async Task Interval_CallbackLongerThanPeriod_NeverOverlaps()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var inFlight = 0;
            var maxInFlight = 0;
            var ticks = 0;
            var maxGate = new object();
            ctx.Interval(() =>
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (maxGate)
                {
                    if (now > maxInFlight) maxInFlight = now;
                }
                Thread.Sleep(80); // longer than the 20ms period
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref ticks);
            }, 20);
            await Task.Delay(400);
            Assert.True(ticks >= 2, $"interval produced {ticks} ticks, expected at least 2");
            Assert.Equal(1, maxInFlight, "callbacks must never overlap");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>The async-iterator overload yields ticks and faults a pending wait when the context unloads.</summary>
    public static async Task IntervalIterator_YieldsTicks_ThenFaultsOnContextDispose()
    {
        var ctx = new Context();
        TimerPlugin.Apply(ctx);
        var enumerator = ctx.Interval(20).GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync(), "first tick");
            Assert.Equal(1, enumerator.Current);
            Assert.True(await enumerator.MoveNextAsync(), "second tick");
            Assert.Equal(2, enumerator.Current);
            var pending = enumerator.MoveNextAsync().AsTask();
            await ctx.DisposeAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
            Assert.Equal("Context has been disposed", error.Message);
        }
        finally
        {
            await enumerator.DisposeAsync();
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Disposing the iterator enumerator ends the iteration gracefully.</summary>
    public static async Task IntervalIterator_EnumeratorDisposal_EndsIterationGracefully()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var enumerator = ctx.Interval(20).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync(), "first tick");
            await enumerator.DisposeAsync();
            Assert.False(await enumerator.MoveNextAsync(), "iteration ended after disposal");
            await ctx.DisposeAsync(); // the effect is already disposed; teardown must not fault
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Throttle runs the first call immediately, schedules one trailing call, and disposal cancels it.</summary>
    public static async Task Throttle_ImmediateThenTrailing_DisposeCancelsPendingTrailing()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var calls = 0;
            var throttled = ctx.Throttle(() => Interlocked.Increment(ref calls), 120);
            throttled.Invoke(); // outside any window: runs immediately
            Assert.Equal(1, calls);
            throttled.Invoke(); // inside the window: schedules a trailing call
            await WaitUntil(() => calls >= 2);
            Assert.Equal(2, calls, "trailing call fired");
            throttled.Invoke(); // inside the window again
            throttled.Dispose(); // cancels the pending trailing call
            await Task.Delay(250);
            Assert.Equal(2, calls, "no trailing call after disposal");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Debounce collapses rapid calls into one and disposal cancels the pending call.</summary>
    public static async Task Debounce_FiresOnceAfterQuietPeriod_DisposeCancelsPending()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var calls = 0;
            var debounced = ctx.Debounce(() => Interlocked.Increment(ref calls), 60);
            debounced.Invoke();
            debounced.Invoke();
            debounced.Invoke();
            await WaitUntil(() => calls >= 1);
            Assert.Equal(1, calls, "debounce fires exactly once");
            debounced.Invoke();
            debounced.Dispose(); // cancels the pending call
            await Task.Delay(150);
            Assert.Equal(1, calls, "no call after disposal");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Delays above the configured cap fail loud at registration.</summary>
    public static async Task DelayAboveConfigCap_FailsLoud()
    {
        var ctx = new Context();
        try
        {
            var service = TimerPlugin.Apply(ctx, new TimerConfig { MaxDelayMs = 1000 });
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => service.Timeout(() => { }, 1001));
            Assert.True(error.Message.Contains("MaxDelayMs", StringComparison.Ordinal));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Interval(() => { }, 1001));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Timeout(() => { }, -1));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>A negative config cap fails loud at plugin application.</summary>
    public static async Task NegativeConfigMaxDelay_FailsLoudAtConstruction()
    {
        var ctx = new Context();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TimerPlugin.Apply(ctx, new TimerConfig { MaxDelayMs = -1 }));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Scheduling on a disposed context fails loud with the inactive-effect error.</summary>
    public static async Task Timeout_OnDisposedContext_ThrowsInactiveEffect()
    {
        var ctx = new Context();
        var service = TimerPlugin.Apply(ctx);
        await ctx.DisposeAsync();
        var error = Assert.Throws<CordisError>(() => service.Timeout(() => { }, 10));
        Assert.Equal(CordisErrorCode.INACTIVE_EFFECT, error.Code);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) return;
            await Task.Delay(10);
        }
    }
}
