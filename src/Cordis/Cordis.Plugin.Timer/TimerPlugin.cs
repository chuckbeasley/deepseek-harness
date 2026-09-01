using Harness.Cordis.Core;
namespace Harness.Cordis.Plugin.Timer;

/// <summary>
/// Plugin entry point (the C# counterpart of applying <c>cordis-plugin-timer</c> through the
/// loader). Applying is idempotent: a second application returns the already-registered service.
/// </summary>
public static class TimerPlugin
{
    /// <summary>
    /// Register the timer service on the current fiber and return it. Constructing a
    /// <see cref="TimerService"/> directly is equivalent.
    /// </summary>
    /// <param name="ctx">the context to register in.</param>
    /// <param name="config">plugin configuration; defaults are applied when omitted.</param>
    /// <returns>the registered service (the existing one when already applied).</returns>
    public static TimerService Apply(Context ctx, TimerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Get<TimerService>("timer") ?? new TimerService(ctx, config);
    }
}
