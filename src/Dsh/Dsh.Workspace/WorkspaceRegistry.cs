using System.Text.Json;
using Cordis.Core;
using Dsh.Session;
using Dsh.Storage;

namespace Dsh.Workspace;

/// <summary>Event names the registry emits after committed mutations (the follow feed's deltas).</summary>
public static class WorkspaceRegistryEvents
{
    /// <summary>One workspace was created or changed (payload: the <see cref="Workspace"/>).</summary>
    public const string Upserted = "workspace/upserted";

    /// <summary>One workspace was deleted (payload: the <see cref="WorkspaceId"/>).</summary>
    public const string Removed = "workspace/removed";

    /// <summary>The display order changed (payload: the ordered <c>string[]</c> of workspace ids).</summary>
    public const string Order = "workspace/order";

    /// <summary>The archive set changed (payload: the <c>string[]</c> of archived session ids).</summary>
    public const string Archived = "workspace/archived";
}

/// <summary>
/// The durable workspace registry (port of the TS WorkspaceRegistry core): every workspace
/// record, its display order, and the archive set live in one storage-backed document and load at
/// service start. Path canonicalization and directory validation match the local lifecycle
/// provider; session membership is explicit (the TS derives it from session-persistence headers,
/// which the C# port does not carry) and arrives through <see cref="AttachSession"/> from the
/// future session/workspace attach flows. Every committed mutation persists, then emits its
/// registry event. The lifecycle provider (ctx.workspace) remains the identity/root core; the
/// registry is the durable catalog the remote namespace sits on.
/// </summary>
public sealed class WorkspaceRegistry : Service
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "workspaceRegistry";

    /// <summary>The storage store name (the storage grammar is lowercase; the service key is camelCase).</summary>
    public const string StoreName = "workspace_registry";

    /// <summary>The storage store format revision.</summary>
    public const int StoreVersion = 1;

    private const string Table = "workspaces";

    private readonly IStorageUnit _store;
    private readonly Dictionary<string, Record> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly List<string> _archived = new();
    private readonly Func<SessionId, bool> _sessionKnown;
    private readonly object _gate = new();

    private sealed class Record
    {
        public required string Id { get; init; }
        public required string Path { get; init; }
        public required string Title { get; set; }
        public required long CreatedAt { get; init; }
        public required long UpdatedAt { get; set; }
        public List<string> SessionIds { get; } = new();
    }

    /// <summary>
    /// Create and register the registry over one storage store.
    /// </summary>
    /// <param name="ctx">the owning context.</param>
    /// <param name="storage">the storage service the document persists through.</param>
    /// <param name="sessionKnown">whether a session id names a real session (live or persisted);
    /// archive requests validate through it. Defaults to accepting any id when no session store is
    /// composed (documented reduction: the TS validates against live sessions plus persistence).</param>
    public WorkspaceRegistry(Context ctx, IStorageService storage, Func<SessionId, bool>? sessionKnown = null)
        : base(ctx, ServiceKey)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _store = storage.Open(new StorageUnitDescriptor(StoreName, StoreVersion, new[] { Table }, HasGlobal: true));
        _sessionKnown = sessionKnown ?? (_ => true);
        Load();
    }

    /// <summary>One workspace by id, or <c>null</c> when absent.</summary>
    public Workspace? Get(WorkspaceId id)
    {
        lock (_gate) return _byId.TryGetValue(id.Value, out var record) ? View(record) : null;
    }

    /// <summary>One workspace by canonical path, or <c>null</c> when none is registered there.</summary>
    public Workspace? ResolveByPath(string path)
    {
        var canonical = TryCanonical(path);
        if (canonical is null) return null;
        lock (_gate) return _byPath.TryGetValue(canonical, out var id) && _byId.TryGetValue(id, out var record) ? View(record) : null;
    }

    /// <summary>Every workspace in display order.</summary>
    public IReadOnlyList<Workspace> List()
    {
        lock (_gate) return _order.Select(id => View(_byId[id])).ToArray();
    }

    /// <summary>The display order (workspace ids).</summary>
    public IReadOnlyList<WorkspaceId> Order()
    {
        lock (_gate) return _order.Select(id => new WorkspaceId(id)).ToArray();
    }

    /// <summary>The archive set (session ids), in archival order.</summary>
    public IReadOnlyList<SessionId> ArchivedSessionIds
    {
        get { lock (_gate) return _archived.Select(id => new SessionId(id)).ToArray(); }
    }

    /// <summary>
    /// Register a workspace over an existing directory. A path already registered resolves to a
    /// rejection with <see cref="WorkspaceErrorCodes.DuplicatePath"/>: the command layer answers
    /// idempotent re-opens through <see cref="ResolveByPath"/>.
    /// </summary>
    public Workspace Create(string path, string? title = null)
    {
        var canonical = Canonical(path);
        if (File.Exists(canonical))
        {
            throw new WorkspaceError($"cannot create a workspace at '{canonical}': path is not a directory", WorkspaceErrorCodes.NotDirectory);
        }
        if (!Directory.Exists(canonical))
        {
            throw new WorkspaceError($"cannot create a workspace at '{canonical}': path does not exist", WorkspaceErrorCodes.NotFound);
        }
        lock (_gate)
        {
            if (_byPath.ContainsKey(canonical))
            {
                throw new WorkspaceError($"a workspace is already registered at '{canonical}'", WorkspaceErrorCodes.DuplicatePath);
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var record = new Record
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = canonical,
                Title = title ?? Path.GetFileName(canonical),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _byId[record.Id] = record;
            _byPath[canonical] = record.Id;
            _order.Add(record.Id);
            PersistRecord(record);
            PersistGlobal();
            Emit(WorkspaceRegistryEvents.Upserted, View(record));
            Emit(WorkspaceRegistryEvents.Order, _order.ToArray());
            return View(record);
        }
    }

    /// <summary>Rename one workspace to a unique non-blank title.</summary>
    public Workspace Rename(WorkspaceId id, string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new WorkspaceError("a workspace title must not be blank", WorkspaceErrorCodes.InvalidTitle);
        }
        lock (_gate)
        {
            var record = Require(id);
            if (_byId.Values.Any(candidate => candidate.Id != id.Value
                && string.Equals(candidate.Title, trimmed, StringComparison.Ordinal)))
            {
                throw new WorkspaceError($"workspace name '{trimmed}' is already in use", WorkspaceErrorCodes.NameConflict);
            }
            record.Title = trimmed;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PersistRecord(record);
            Emit(WorkspaceRegistryEvents.Upserted, View(record));
            return View(record);
        }
    }

    /// <summary>Remove one workspace registration while retaining files and sessions.</summary>
    /// <returns>false when no workspace carries the id.</returns>
    public bool Delete(WorkspaceId id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id.Value, out var record)) return false;
            _byId.Remove(id.Value);
            _byPath.Remove(record.Path);
            _order.Remove(id.Value);
            _store.Delete(Table, id.Value);
            PersistGlobal();
            Emit(WorkspaceRegistryEvents.Removed, id);
            Emit(WorkspaceRegistryEvents.Order, _order.ToArray());
            return true;
        }
    }

    /// <summary>
    /// Move one workspace within the display order, DOM-insertBefore-like: before
    /// <paramref name="beforeId"/> when given, else to the end.
    /// </summary>
    public IReadOnlyList<WorkspaceId> InsertBefore(WorkspaceId id, WorkspaceId? beforeId)
    {
        lock (_gate)
        {
            Require(id);
            if (beforeId is { } before)
            {
                if (before == id)
                {
                    throw new WorkspaceError($"cannot move workspace '{id}' before itself", WorkspaceErrorCodes.OrderInvalid);
                }
                Require(before);
                _order.Remove(id.Value);
                _order.Insert(_order.IndexOf(before.Value), id.Value);
            }
            else
            {
                _order.Remove(id.Value);
                _order.Add(id.Value);
            }
            PersistGlobal();
            Emit(WorkspaceRegistryEvents.Order, _order.ToArray());
            return _order.Select(value => new WorkspaceId(value)).ToArray();
        }
    }

    /// <summary>
    /// Account one session into a workspace's membership (the entry point of the future
    /// session/workspace attach flows; the TS derives membership from session-persistence headers).
    /// </summary>
    public Workspace AttachSession(WorkspaceId workspaceId, SessionId sessionId)
    {
        lock (_gate)
        {
            var record = Require(workspaceId);
            if (!record.SessionIds.Contains(sessionId.Value, StringComparer.Ordinal))
            {
                record.SessionIds.Add(sessionId.Value);
                record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PersistRecord(record);
            }
            Emit(WorkspaceRegistryEvents.Upserted, View(record));
            return View(record);
        }
    }

    /// <summary>Move one accounted session within a workspace's membership order.</summary>
    public Workspace InsertSessionBefore(WorkspaceId workspaceId, SessionId sessionId, SessionId? beforeSessionId)
    {
        lock (_gate)
        {
            var record = Require(workspaceId);
            if (!record.SessionIds.Contains(sessionId.Value, StringComparer.Ordinal)
                || (beforeSessionId is { } before && !record.SessionIds.Contains(before.Value, StringComparer.Ordinal)))
            {
                throw new WorkspaceError(
                    $"cannot move session '{sessionId}' within workspace '{workspaceId}': a moved session must be a member",
                    WorkspaceErrorCodes.MoveInvalid);
            }
            if (beforeSessionId is { } anchor && anchor != sessionId)
            {
                record.SessionIds.Remove(sessionId.Value);
                record.SessionIds.Insert(record.SessionIds.IndexOf(anchor.Value), sessionId.Value);
                record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PersistRecord(record);
            }
            Emit(WorkspaceRegistryEvents.Upserted, View(record));
            return View(record);
        }
    }

    /// <summary>Hide one known session from workspace grouping surfaces.</summary>
    public void ArchiveSession(SessionId sessionId)
    {
        if (!_sessionKnown(sessionId))
        {
            throw new WorkspaceError($"cannot archive session '{sessionId}': no live or persisted session carries that id", WorkspaceErrorCodes.UnknownSession);
        }
        lock (_gate)
        {
            if (_archived.Contains(sessionId.Value, StringComparer.Ordinal)) return;
            _archived.Add(sessionId.Value);
            PersistGlobal();
            Emit(WorkspaceRegistryEvents.Archived, _archived.ToArray());
        }
    }

    /// <summary>Release the storage store during teardown.</summary>
    public override ValueTask StopAsync()
    {
        _store.Close();
        return ValueTask.CompletedTask;
    }

    private Record Require(WorkspaceId id)
        => _byId.TryGetValue(id.Value, out var record)
            ? record
            : throw new WorkspaceError($"workspace \"{id}\" not found", WorkspaceErrorCodes.NotFound);

    private static Workspace View(Record record)
        => new(
            new WorkspaceId(record.Id),
            record.Path,
            record.Title,
            DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAt),
            DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAt),
            record.SessionIds.Select(id => new SessionId(id)).ToArray());

    private void PersistRecord(Record record)
    {
        var json = new Dictionary<string, object?>
        {
            ["id"] = record.Id,
            ["path"] = record.Path,
            ["title"] = record.Title,
            ["createdAt"] = record.CreatedAt,
            ["updatedAt"] = record.UpdatedAt,
            ["sessionIds"] = record.SessionIds,
        };
        _store.Set(Table, record.Id, JsonSerializer.SerializeToElement(json));
    }

    private void PersistGlobal()
    {
        _store.SetGlobal(JsonSerializer.SerializeToElement(new
        {
            order = _order,
            archivedSessionIds = _archived,
        }));
    }

    private void Load()
    {
        var snapshot = _store.LoadAll();
        if (snapshot.Tables.TryGetValue(Table, out var records))
        {
            foreach (var pair in records)
            {
                using var document = JsonDocument.Parse(pair.Value.GetRawText());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("createdAt", out var createdAt) || !createdAt.TryGetInt64(out var created)
                    || !root.TryGetProperty("updatedAt", out var updatedAt) || !updatedAt.TryGetInt64(out var updated))
                {
                    Ctx.Logger.Warn($"workspaceRegistry: skipping a malformed record \"{pair.Key}\"");
                    continue;
                }
                var record = new Record
                {
                    Id = id.GetString()!,
                    Path = path.GetString()!,
                    Title = title.GetString()!,
                    CreatedAt = created,
                    UpdatedAt = updated,
                };
                if (root.TryGetProperty("sessionIds", out var sessionIds) && sessionIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sessionIds.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String) record.SessionIds.Add(item.GetString()!);
                    }
                }
                _byId[record.Id] = record;
                _byPath[record.Path] = record.Id;
            }
        }
        if (snapshot.Global is JsonElement global && global.ValueKind == JsonValueKind.Object)
        {
            if (global.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in order.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && _byId.ContainsKey(item.GetString()!)) _order.Add(item.GetString()!);
                }
            }
            if (global.TryGetProperty("archivedSessionIds", out var archived) && archived.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in archived.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) _archived.Add(item.GetString()!);
                }
            }
        }
        // Any workspace absent from the persisted order appends in id order (a torn or hand-edited
        // document never hides a registered workspace).
        foreach (var id in _byId.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!_order.Contains(id)) _order.Add(id);
        }
    }

    private void Emit(string name, object payload)
    {
        try
        {
            Ctx.Emit(name, payload);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"workspaceRegistry: a {name} listener threw: {error.Message}");
        }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkspaceError("cannot register a workspace at an empty path", WorkspaceErrorCodes.InvalidPath);
        }
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceError($"cannot resolve \"{path}\": invalid path", WorkspaceErrorCodes.InvalidPath, error);
        }
    }

    private static string? TryCanonical(string path)
    {
        try
        {
            return Canonical(path);
        }
        catch (WorkspaceError)
        {
            return null;
        }
    }
}
