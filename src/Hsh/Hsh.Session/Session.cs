using System.Text.Json.Serialization;
using Harness.Llm;

namespace Harness.Session;

/// <summary>Identifies one session in the store (and its persistence artifacts).</summary>
[JsonConverter(typeof(StringIdJsonConverter<SessionId>))]
public readonly record struct SessionId(string Value) : IStringId
{
    public static implicit operator string(SessionId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Session format constants.</summary>
public static class SessionFormat
{
    /// <summary>The on-disk session format version; pinned at 0 while the harness is unreleased.</summary>
    public const int Version = 0;
}

/// <summary>Immutable validated storage metadata, kept outside the conversation event log.</summary>
public sealed record SessionHeader(
    int Version,
    SessionId Id,
    long CreatedAtMs,
    string Cwd,
    int DelegationDepth,
    string? ParentSessionId = null,
    string? Origin = null,
    int? SeedLength = null);

/// <summary>
/// An event-sourced session: an append-only log of <see cref="SessionEvent"/>s.
/// <see cref="Append"/> stamps the envelope (Id = "evt-{seq}", Seq = log length, TimeMs = now) and
/// publishes through the attached store. There is no update, delete, or reorder API — records are
/// immutable by construction and the log is the source of truth.
/// </summary>
public sealed class Session
{
    private readonly List<SessionEvent> _log = new();
    private IReadOnlyList<SessionEvent>? _eventsSnapshot;
    private readonly SessionStore? _store;

    internal Session(SessionId id, SessionStore? store, string cwd, int delegationDepth = 0, string? parentSessionId = null, string? origin = null)
    {
        _store = store;
        Header = new SessionHeader(
            SessionFormat.Version, id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cwd, delegationDepth, parentSessionId, origin);
    }

    /// <summary>Detached creation metadata (format version, identity, creation time).</summary>
    public SessionHeader Header { get; }

    /// <summary>The session identity, derived from its header's single copy.</summary>
    public SessionId Id => Header.Id;

    /// <summary>
    /// A frozen snapshot of the append-only log. The snapshot is reused until the next append; a
    /// previously returned array never grows later.
    /// </summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get
        {
            _eventsSnapshot ??= _log.ToArray();
            return _eventsSnapshot;
        }
    }

    /// <summary>The next event's sequence number — always the log length (the seq = log.Length contract).</summary>
    public long Seq => _log.Count;

    /// <summary>
    /// Append one typed event: stamps the envelope, commits it, and notifies observers through the
    /// attached store's contained publication. A non-JSON-serializable event is rejected at the
    /// append site (the log is the durable source of truth).
    /// </summary>
    /// <returns>the logged event with its assigned envelope.</returns>
    public SessionEvent Append(SessionEvent candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var seq = _log.Count;
        var assigned = candidate with
        {
            Id = $"evt-{seq}",
            Seq = seq,
            TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _log.Add(assigned);
        _eventsSnapshot = null;
        _store?.Publish(this, assigned);
        return assigned;
    }

    /// <summary>
    /// Rehydrate a stored log into this live session (the resume path): each stored event keeps
    /// its envelope (Id, Seq, TimeMs) and appends in order without re-publishing, so observers
    /// see replayed events through <see cref="Events"/> rather than as fresh appends.
    /// </summary>
    /// <param name="stored">the stored events, in log order.</param>
    /// <exception cref="InvalidOperationException">when a stored event's seq breaks the seq = log-length contract.</exception>
    public void Restore(IReadOnlyList<SessionEvent> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        foreach (var evt in stored)
        {
            if (evt.Seq != _log.Count)
            {
                throw new InvalidOperationException(
                    $"cannot restore session \"{Id}\": stored event seq {evt.Seq} breaks the seq = log-length contract (expected {_log.Count})");
            }
            _log.Add(evt);
        }
        _eventsSnapshot = null;
    }

    /// <summary>
    /// Derive the LLM message history by folding the log through the surface projection
    /// (user/message -> verbatim; assistant/message -> null when empty; tool/result -> message;
    /// a replace-op checkpoint shadows its range in place).
    /// </summary>
    public IReadOnlyList<Message> DeriveMessages()
        => Surface.Fold(_log).Select(node => node.Message).ToArray();
}

