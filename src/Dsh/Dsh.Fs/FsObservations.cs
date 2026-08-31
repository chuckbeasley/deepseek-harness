namespace Dsh.Fs;

/// <summary>One authoritative presence/absence observation of a target (port of the TS <c>FsObservation</c>).</summary>
public enum FsObservedKind
{
    /// <summary>The target existed at the recorded version.</summary>
    Present,

    /// <summary>The target was confirmed absent.</summary>
    Absent,
}

/// <summary>An observation record: the discriminant plus the observed version (empty for absent).</summary>
public sealed record FsObserved(FsObservedKind Kind, FsVersion Version);

/// <summary>
/// Per-session filesystem observation state (port of the fs-observation-policy gate minus the
/// plugin wiring): reads and successful mutations record presence/absence per target, and the
/// write/edit consumers derive their guarded intents from that state. A session with no
/// observations keeps the provider's unconditional mutation behavior.
/// </summary>
public sealed class FsObservations
{
    private readonly Dictionary<string, Dictionary<string, FsObserved>> _bySession = new(StringComparer.Ordinal);

    /// <summary>Record one authoritative observation for the owning session (no session = no-op).</summary>
    public void Observe(string? sessionId, string targetKey, FsObserved observed)
    {
        if (sessionId is null) return;
        var byTarget = _bySession.GetValueOrDefault(sessionId);
        if (byTarget is null)
        {
            byTarget = new Dictionary<string, FsObserved>(StringComparer.Ordinal);
            _bySession[sessionId] = byTarget;
        }
        byTarget[targetKey] = observed;
    }

    /// <summary>The last observation for one session/target, or <c>null</c> when the target was never observed.</summary>
    public FsObserved? Get(string? sessionId, string targetKey)
        => sessionId is null ? null : _bySession.GetValueOrDefault(sessionId)?.GetValueOrDefault(targetKey);
}