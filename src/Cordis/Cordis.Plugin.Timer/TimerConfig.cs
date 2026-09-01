namespace Harness.Cordis.Plugin.Timer;

/// <summary>
/// Plugin configuration for the timer service (the C# counterpart of the plugin Config surface).
/// Every scheduling bound is a validated config field so deployments can raise or lower it from
/// their config file without code changes; the TS plugin had no config because its only bound was
/// the Node host timer clamp, which this port makes explicit.
/// </summary>
public sealed record TimerConfig
{
    /// <summary>
    /// Upper bound, in milliseconds, for every <c>delay</c> accepted by the scheduling methods.
    /// Default <c>2147483647</c> — the Node <c>setTimeout</c>/<c>setInterval</c> clamp boundary,
    /// roughly 24.8 days. The TS plugin silently clamped larger delays to 1ms via the host timer;
    /// the port fails loud at registration instead (a documented deviation).
    /// </summary>
    public long MaxDelayMs { get; init; } = int.MaxValue;
}
