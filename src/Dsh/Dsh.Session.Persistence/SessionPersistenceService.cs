using Cordis.Core;
using Dsh.Session;

namespace Dsh.Session.Persistence;

/// <summary>One stored session's immutable metadata and replayed event log.</summary>
/// <param name="Header">the format envelope parsed from the log's first line.</param>
/// <param name="Events">the replayed events, in log order.</param>
public sealed record StoredSession(SessionHeader Header, IReadOnlyList<SessionEvent> Events);

/// <summary>
/// JSONL durable session-persistence backend (ctx.sessionPersistence). It stores one session
/// event per JSON line in one append-only file per session; the file's first line is a
/// format-version header envelope. <see cref="Append"/> writes a batch, <see cref="Load"/> replays
/// a file back into identical event objects, and <see cref="Attach(Session, Action{StoredSession}?)"/>
/// wires a live session to its log: the store persists on append and the stored log loads on
/// attach. File path and flush policy come from <see cref="PersistenceConfig"/> with documented
/// defaults — no tunable is hardcoded.
/// </summary>
public sealed class SessionPersistenceService : Service
{
    private readonly PersistenceConfig _config;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<SessionId, PendingWrite> _pending = new();
    private System.Threading.Timer? _flushTimer;

    /// <summary>
    /// Create the backend and register it as <c>sessionPersistence</c> on the context.
    /// </summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <param name="config">root directory and flush policy (see <see cref="PersistenceConfig"/>).</param>
    /// <exception cref="ArgumentException">when the configured root is empty.</exception>
    public SessionPersistenceService(Context ctx, PersistenceConfig config)
        : base(ctx, "sessionPersistence")
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Root))
        {
            throw new ArgumentException("persistence root must not be empty", nameof(config));
        }
    }

    /// <summary>The append-only log file path for a session (side-effect-free).</summary>
    public string LogPath(SessionId id) => JsonlFormat.LogPath(_config.Root, id);

    /// <summary>Whether a stored log exists for the session.</summary>
    public bool Exists(SessionId id) => File.Exists(LogPath(id));

    /// <summary>
    /// Replay a session's stored log back into identical event objects: the header envelope is
    /// parsed and version-checked, then every event line is deserialized with the same
    /// polymorphic System.Text.Json handling the session tests use.
    /// </summary>
    /// <param name="id">the session to read.</param>
    /// <returns>the stored header and events, or <c>null</c> when the session has no stored log.</returns>
    /// <exception cref="SessionFormatUnsupportedException">when the log's format version is foreign.</exception>
    /// <exception cref="JsonException">when the log is structurally corrupt (bad header, mismatched id, or a bad event line).</exception>
    public StoredSession? Load(SessionId id)
    {
        var path = LogPath(id);
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            throw new JsonException($"corrupt session log \"{path}\": empty file");
        }
        var header = JsonlFormat.ParseHeaderLine(lines[0], id);
        var events = new List<SessionEvent>(lines.Length - 1);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                throw new JsonException($"corrupt session log \"{path}\": blank line at line {index + 1}");
            }
            var evt = JsonSerializer.Deserialize<SessionEvent>(line, SessionEventTypes.CreateSerializerOptions());
            if (evt is null)
            {
                throw new JsonException($"corrupt session log \"{path}\": unparsable event at line {index + 1}");
            }
            events.Add(evt);
        }
        return new StoredSession(header, events);
    }

    /// <summary>
    /// Durably append one batch of committed events to the session's log, materializing the
    /// header line on the file's first write. Under <see cref="FlushMode.SyncAppend"/> the write is
    /// flushed to disk before this method returns; under <see cref="FlushMode.Batched"/> the batch
    /// is buffered until the flush interval elapses or <see cref="Flush"/> runs.
    /// </summary>
    /// <param name="id">the session owning the events.</param>
    /// <param name="header">the session's immutable metadata; written as the envelope on materialization.</param>
    /// <param name="events">the events to append, in log order.</param>
    public void Append(SessionId id, SessionHeader header, IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;
        if (_config.FlushMode == FlushMode.Batched)
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(id, out var pending))
                {
                    pending = new PendingWrite { Header = header };
                    _pending[id] = pending;
                }
                pending.Events.AddRange(events);
                EnsureFlushTimer();
            }
            return;
        }
        WriteBatch(id, header, events);
    }

    /// <summary>
    /// Attach one live session to its log: the stored log (when one exists) is loaded and handed
    /// to <paramref name="onLoad"/>, and every subsequent append on the session is persisted. The
    /// returned disposer detaches the session.
    /// </summary>
    /// <param name="session">the live session to persist.</param>
    /// <param name="onLoad">optional receiver of the stored log loaded on attach.</param>
    /// <returns>the detach disposer.</returns>
    public IDisposable Attach(Session session, Action<StoredSession>? onLoad = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stored = Load(session.Id);
        if (stored is not null) onLoad?.Invoke(stored);
        var subscription = Ctx.On("session/event",
            (Delegate)(Action<Session, SessionEvent>)((owner, evt) =>
            {
                if (owner.Id != session.Id) return;
                Append(session.Id, session.Header, new[] { evt });
            }));
        return new DisposableAction(subscription.Dispose);
    }

    /// <summary>
    /// Attach the whole store: every append on every session the store owns is persisted to that
    /// session's log. Use <see cref="Attach(Session, Action{StoredSession}?)"/> for the load-on-attach
    /// replay path of one session.
    /// </summary>
    /// <param name="store">the session store whose appends are persisted.</param>
    /// <returns>the detach disposer.</returns>
    public IDisposable Attach(SessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Ctx.On("session/event",
            (Delegate)(Action<Session, SessionEvent>)((session, evt) =>
                Append(session.Id, session.Header, new[] { evt })));
    }

    /// <summary>
    /// Enumerate every stored session header (the ACP session list/resume surface). A zero-byte
    /// log (a crash mid-write) is skipped; a malformed non-empty log fails loud like a single
    /// load would.
    /// </summary>
    /// <returns>the stored headers in filesystem order.</returns>
    public IReadOnlyList<SessionHeader> ListHeaders()
    {
        var results = new List<SessionHeader>();
        if (!Directory.Exists(_config.Root)) return results;
        foreach (var directory in Directory.EnumerateDirectories(_config.Root))
        {
            var logPath = Path.Combine(directory, JsonlFormat.LogFileName);
            if (!File.Exists(logPath)) continue;
            var firstLine = ReadFirstLine(logPath);
            if (firstLine is null) continue;
            results.Add(ParseListedHeader(firstLine, logPath));
        }
        return results;
    }

    private static string? ReadFirstLine(string path)
    {
        using var reader = new StreamReader(path, new System.Text.UTF8Encoding(false));
        return reader.ReadLine();
    }

    private static SessionHeader ParseListedHeader(string line, string logPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.GetString() != JsonlFormat.HeaderType
            || !root.TryGetProperty("version", out var version) || version.ValueKind != System.Text.Json.JsonValueKind.Number
            || !root.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String
            || !root.TryGetProperty("createdAt", out var createdAt) || createdAt.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            throw new InvalidOperationException($"corrupt session log: first line of {logPath} is not a session header");
        }
        var formatVersion = version.GetInt32();
        var parsedId = new SessionId(id.GetString()!);
        if (formatVersion != SessionFormat.Version)
        {
            throw new SessionFormatUnsupportedException(
                $"session log \"{parsedId}\" uses format version {formatVersion}, but this build reads format version {SessionFormat.Version}");
        }
        return new SessionHeader(formatVersion, parsedId, createdAt.GetInt64());
    }

    /// <summary>Flush every buffered batch to disk (a no-op under <see cref="FlushMode.SyncAppend"/>).</summary>
    public void Flush()
    {
        KeyValuePair<SessionId, PendingWrite>[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var (id, pending) in batch)
        {
            WriteBatch(id, pending.Header, pending.Events);
        }
    }

    /// <summary>Flush pending batches and stop the flush timer during teardown.</summary>
    public override ValueTask StopAsync()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        try
        {
            Flush();
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"sessionPersistence: final flush failed: {error.Message}");
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Create the batched-flush timer once, on the first buffered append.</summary>
    private void EnsureFlushTimer()
    {
        if (_flushTimer is not null) return;
        _flushTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    Flush();
                }
                catch (Exception error)
                {
                    Ctx.Logger.Warn($"sessionPersistence: batched flush failed: {error.Message}");
                }
            },
            null,
            TimeSpan.FromMilliseconds(_config.BatchDelayMs),
            TimeSpan.FromMilliseconds(_config.BatchDelayMs));
    }

    /// <summary>Append one batch's lines to the session's log, materializing the header when absent.</summary>
    private void WriteBatch(SessionId id, SessionHeader header, IReadOnlyList<SessionEvent> events)
    {
        // The batched-flush timer and an explicit Flush can run concurrently; serialize the
        // exists-check + write so a race cannot materialize two header lines.
        lock (_writeGate)
        {
            var path = LogPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var materialize = !File.Exists(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (materialize)
            {
                writer.WriteLine(JsonlFormat.HeaderLine(header));
            }
            foreach (var evt in events)
            {
                writer.WriteLine(JsonSerializer.Serialize<SessionEvent>(evt, SessionEventTypes.CreateSerializerOptions()));
            }
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>One session's buffered batch under <see cref="FlushMode.Batched"/>.</summary>
    private sealed class PendingWrite
    {
        public required SessionHeader Header { get; init; }

        public List<SessionEvent> Events { get; } = new();
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action (Cordis.Core's is internal).</summary>
internal sealed class DisposableAction : IDisposable
{
    private readonly Action _action;

    public DisposableAction(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
