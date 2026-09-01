namespace Harness.Context;

/// <summary>
/// Request clock contributor (port of time-context): the current time sampled from an injectable
/// clock, rendered in the timestamp.ts ISO shape. The TS projection fold (lastMessageTime /
/// lastInjectionTime), browser time-zone derivation, and refresh scheduling are deferred (named,
/// not ported); the injectable clock replaces Date.now() so tests are deterministic.
/// </summary>
public sealed class TimeContextContributor : IContextContributor
{
    /// <summary>The contributor's stable key.</summary>
    public const string DefaultKey = "time-context";

    private readonly Func<DateTimeOffset> _clock;
    private readonly string _timeZone;

    /// <summary>Create the contributor over an injectable clock.</summary>
    /// <param name="clock">time source; defaults to the process clock.</param>
    /// <param name="timeZone">display zone label carried in the timestamp; defaults to "UTC".</param>
    public TimeContextContributor(Func<DateTimeOffset>? clock = null, string? timeZone = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _timeZone = timeZone ?? "UTC";
    }

    /// <inheritdoc />
    public string Key => DefaultKey;

    /// <inheritdoc />
    public Task<ContextSection?> ContributeAsync(Harness.Session.Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock();
        return Task.FromResult<ContextSection?>(
            new ContextSection(Key, $"Time sampled while preparing context: {FormatTimestamp(now, _timeZone)}"));
    }

    /// <summary>Format an instant in the timestamp.ts ISO shape: yyyy-MM-ddTHH:mm:ss±HH:mm[zone].</summary>
    public static string FormatTimestamp(DateTimeOffset now, string timeZone)
        => $"{now:yyyy-MM-dd'T'HH:mm:ss}{now:zzz}[{timeZone}]";
}
