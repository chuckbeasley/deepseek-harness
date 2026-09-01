using Harness.Cordis.Core;
namespace Harness.Cordis.Plugin.Timer;

/// <summary>
/// Timer surface mixed into contexts by the plugin (the C# counterpart of the TS
/// <c>ctx.mixin('timer', [...])</c> proxy: extension methods read the registered
/// <see cref="TimerService"/>). Calling any member before the plugin is applied fails loud with
/// <see cref="Context.Require{T}(string)"/>.
/// </summary>
public static class TimerContextExtensions
{
    /// <summary>The timer service registered by <see cref="TimerPlugin.Apply(Context, TimerConfig?)"/>.</summary>
    public static TimerService Timer(this Context ctx) => ctx.Require<TimerService>("timer");

    /// <inheritdoc cref="TimerService.Timeout(Action, long)"/>
    public static IDisposable Timeout(this Context ctx, Action callback, long delayMs) => ctx.Timer().Timeout(callback, delayMs);

    /// <inheritdoc cref="TimerService.TimeoutAsync(long)"/>
    public static Task TimeoutAsync(this Context ctx, long delayMs) => ctx.Timer().TimeoutAsync(delayMs);

    /// <inheritdoc cref="TimerService.Interval(Action, long)"/>
    public static IDisposable Interval(this Context ctx, Action callback, long delayMs) => ctx.Timer().Interval(callback, delayMs);

    /// <inheritdoc cref="TimerService.Interval(long)"/>
    public static IAsyncEnumerable<int> Interval(this Context ctx, long delayMs) => ctx.Timer().Interval(delayMs);

    /// <inheritdoc cref="TimerService.Throttle(Action, long, bool)"/>
    public static ThrottledAction Throttle(this Context ctx, Action callback, long delayMs, bool noTrailing = false)
        => ctx.Timer().Throttle(callback, delayMs, noTrailing);

    /// <inheritdoc cref="TimerService.Debounce(Action, long)"/>
    public static DebouncedAction Debounce(this Context ctx, Action callback, long delayMs)
        => ctx.Timer().Debounce(callback, delayMs);

    /// <inheritdoc cref="TimerService.SetTimeout(Action, long)"/>
    public static IDisposable SetTimeout(this Context ctx, Action callback, long delayMs) => ctx.Timer().SetTimeout(callback, delayMs);

    /// <inheritdoc cref="TimerService.SetInterval(Action, long)"/>
    public static IDisposable SetInterval(this Context ctx, Action callback, long delayMs) => ctx.Timer().SetInterval(callback, delayMs);
}
