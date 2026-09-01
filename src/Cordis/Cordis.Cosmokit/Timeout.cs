namespace Harness.Cordis.Cosmokit;

/// <summary>
/// Internal abort reason carrying a capability-owned code and elapsed deadline,
/// mirroring the <c>TimeoutReason</c> from <c>@deepseek-ai/dsh-timeout</c>.
/// </summary>
public sealed class TimeoutReason : Exception
{
    /// <summary>Capability-owned timeout code (e.g. <c>BASH_TIMEOUT</c>).</summary>
    public string Code { get; }

    /// <summary>The deadline that elapsed, in milliseconds.</summary>
    public double TimeoutMs { get; }

    /// <summary>Creates a reason for <paramref name="code"/> after <paramref name="timeoutMs"/> milliseconds.</summary>
    public TimeoutReason(string code, double timeoutMs)
        : base($"{code} after {timeoutMs}ms")
    {
        Code = code;
        TimeoutMs = timeoutMs;
    }
}

/// <summary>
/// A deadline signal plus the cleanup that clears its timer, mirroring the
/// <c>deadline</c> helper from <c>@deepseek-ai/dsh-timeout</c>. The token only
/// notifies; the caller owns the mechanism that stops its work.
/// </summary>
public sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly System.Threading.Timer? _timer;
    private readonly CancellationTokenRegistration _registration;
    private TimeoutReason? _reason;
    private int _disposed;

    private Deadline(CancellationToken? upstream, double timeoutMs, string code)
    {
        _cts = new CancellationTokenSource();
        if (upstream is { CanBeCanceled: true } token)
        {
            _registration = token.Register(() => _cts.Cancel());
        }
        if (timeoutMs > 0)
        {
            if (!double.IsFinite(timeoutMs) || timeoutMs > MaxTimerDelayMs)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), $"deadline timeoutMs must be a positive finite number no greater than {MaxTimerDelayMs}");
            }
            var reason = new TimeoutReason(code, timeoutMs);
            _timer = new System.Threading.Timer(
                _ =>
                {
                    _reason = reason;
                    _cts.Cancel();
                },
                null,
                TimeSpan.FromMilliseconds(timeoutMs),
                System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Largest delay scheduled without clamping, matching Node's timer ceiling.</summary>
    public const double MaxTimerDelayMs = 2_147_483_647;

    /// <summary>Cancels on upstream cancellation OR on timeout (the timeout carries a <see cref="TimeoutReason"/>).</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The recorded timeout reason, set when the timer fires; <c>null</c> otherwise.</summary>
    public TimeoutReason? Reason => _reason;

    /// <summary>
    /// Fuses upstream cancellation with an identifiable timeout.
    /// <paramref name="timeoutMs"/> <c>&lt;= 0</c> arms no timer (background
    /// work): only the upstream signal is forwarded.
    /// </summary>
    /// <param name="upstream">The caller's cancellation signal, if any.</param>
    /// <param name="timeoutMs">Deadline in milliseconds; <c>&lt;= 0</c> means no timeout.</param>
    /// <param name="code">Capability-owned code stamped onto the timeout's <see cref="TimeoutReason"/>.</param>
    /// <returns>The fused <see cref="Deadline"/> (token + timer cleanup).</returns>
    public static Deadline Create(CancellationToken? upstream, double timeoutMs, string code) => new(upstream, timeoutMs, code);

    /// <summary>Clears an armed timer. Safe to call once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _registration.Dispose();
    }
}

/// <summary>Shared timeout arithmetic and classification (port of <c>@deepseek-ai/dsh-timeout</c>).</summary>
public static class Timeout
{
    /// <summary>
    /// Validates a caller's optional timeout hint, applies a backend default,
    /// then caps it. Supplied values must be positive and finite; zero is not a
    /// disable-timeout sentinel.
    /// </summary>
    /// <param name="requested">The caller's optional hint; validated when present.</param>
    /// <param name="def">The backend default applied when <paramref name="requested"/> is absent.</param>
    /// <param name="max">The backend upper bound the result is capped to.</param>
    /// <param name="name">Field name used in the thrown message.</param>
    /// <returns>The effective timeout in milliseconds: <c>min(requested ?? def, max)</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="requested"/> is not a positive finite number.</exception>
    public static double ClampTimeout(double? requested, double def, double max, string name = "timeoutMs")
    {
        if (requested is { } value && (!double.IsFinite(value) || value <= 0))
        {
            throw new ArgumentException($"{name} must be a positive finite number", name);
        }
        return Math.Min(requested ?? def, max);
    }

    /// <summary>
    /// Recover the timeout reason recorded by a <see cref="Deadline"/>.
    /// Supplying <paramref name="code"/> distinguishes this deadline from a
    /// nested upstream deadline.
    /// </summary>
    /// <param name="deadline">The deadline whose timer may have fired.</param>
    /// <param name="code">When provided, only a <see cref="TimeoutReason"/> with this exact code matches.</param>
    /// <returns>The matching <see cref="TimeoutReason"/>, else <c>null</c>.</returns>
    public static TimeoutReason? TimeoutOf(Deadline deadline, string? code = null)
    {
        var reason = deadline.Reason;
        if (reason is null) return null;
        return code is null || reason.Code == code ? reason : null;
    }
}

