using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Cordis.Core;

namespace Harness.Storage;

/// <summary>Configuration for the JSON file backend: the directory holding one <c>&lt;unit&gt;.json</c> file per store.</summary>
/// <remarks>
/// <c>Root</c> has NO default on purpose: a <c>Directory.GetCurrentDirectory()</c> fallback would
/// scatter store files wherever the process happens to start; assemblies state the location
/// explicitly (mirrors the storage-json plugin Config).
/// </remarks>
public sealed record JsonFileStorageConfig(string Root);

/// <summary>
/// JSON file provider for ctx.storage (port of packages/storage/storage-json, <c>single</c> layout
/// only): one human-readable document per scoped store at <c>&lt;root&gt;/&lt;name&gt;.json</c>,
/// published by atomic replace (same-directory temp file, flush-to-disk, then rename over the
/// target; replacement is last-write-wins — one writer per store, matching the TS backend). The
/// in-memory state is authoritative; every write mutates it and republishes the whole file.
///
/// Deferred seams, named here: the <c>per-record</c> layout (one version-stamped document per
/// record), the storage-sqlite backend, and the storage-domain data-form layer (domain specs,
/// tables, and global state on top of this medium) are not ported in this phase.
///
/// The file format matches storage-json: <c>{ "unit": { "name", "version" }, "global",
/// "tables": { &lt;table&gt;: { &lt;key&gt;: value } } }</c>, pretty-printed with a trailing newline.
/// A corrupt file (not valid JSON, a foreign unit header, or a table that is not an object) fails
/// loud with STORAGE_MALFORMED_MEDIUM; a stored version differing from the descriptor rejects with
/// STORAGE_VERSION_MISMATCH.
/// </summary>
public sealed class JsonFileStorageProvider : Service, IStorageService
{
    private static readonly Regex UnitNameRegex = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly string _root;
    private readonly Dictionary<string, JsonStorageUnit> _open = new(StringComparer.Ordinal);
    private bool _closed;

    /// <summary>Register the provider as ctx.storage over <paramref name="config"/>.Root; the root directory is created when missing.</summary>
    public JsonFileStorageProvider(Context ctx, JsonFileStorageConfig config)
        : base(ctx, "storage")
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Root))
        {
            throw new ArgumentException("storage root must be a non-empty path", nameof(config));
        }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root));
        Directory.CreateDirectory(_root);
    }

    /// <summary>The normalized root directory holding one <c>&lt;unit&gt;.json</c> file per store.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public IStorageUnit Open(StorageUnitDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ThrowIfClosed();
        ValidateDescriptor(descriptor);
        if (_open.ContainsKey(descriptor.Name))
        {
            throw new StorageError(
                $"unit '{descriptor.Name}' is already open; a unit has exactly one live handle",
                StorageErrorCodes.AlreadyOpen);
        }

        var path = Path.Combine(_root, $"{descriptor.Name}.json");
        var state = File.Exists(path) ? Parse(File.ReadAllText(path), descriptor) : new UnitState(descriptor);
        var unit = new JsonStorageUnit(descriptor, path, state, () => _open.Remove(descriptor.Name));
        _open[descriptor.Name] = unit;
        return unit;
    }

    /// <inheritdoc />
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        foreach (var unit in _open.Values.ToArray())
        {
            unit.Close();
        }
        _open.Clear();
    }

    /// <summary>Close every open store during context teardown (idempotent with <see cref="Close"/>).</summary>
    public override ValueTask StopAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new StorageError("json storage backend is closed", StorageErrorCodes.Closed);
        }
    }

    private static void ValidateDescriptor(StorageUnitDescriptor descriptor)
    {
        if (!UnitNameRegex.IsMatch(descriptor.Name))
        {
            throw new StorageError($"invalid unit name '{descriptor.Name}'", StorageErrorCodes.InvalidName);
        }
        foreach (var table in descriptor.Tables)
        {
            if (!UnitNameRegex.IsMatch(table))
            {
                throw new StorageError($"invalid table name '{table}' in unit '{descriptor.Name}'", StorageErrorCodes.InvalidName);
            }
        }
    }

    /// <summary>Parse a unit file into state, validating shape and revision (port of storage-json format.parse).</summary>
    private static UnitState Parse(string text, StorageUnitDescriptor descriptor)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException error)
        {
            throw new StorageError($"unit '{descriptor.Name}': file is not valid JSON", StorageErrorCodes.MalformedMedium, error);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new StorageError($"unit '{descriptor.Name}': file is not a JSON object", StorageErrorCodes.MalformedMedium);
            }
            if (!root.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.Object
                || !unit.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != descriptor.Name
                || !unit.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number)
            {
                throw new StorageError($"unit '{descriptor.Name}': missing or foreign unit header", StorageErrorCodes.MalformedMedium);
            }
            if (!version.TryGetInt32(out var storedVersion))
            {
                throw new StorageError($"unit '{descriptor.Name}': stored version is not an integer", StorageErrorCodes.MalformedMedium);
            }
            if (storedVersion != descriptor.Version)
            {
                throw new StorageError(
                    $"unit '{descriptor.Name}': stored version {storedVersion} != expected {descriptor.Version}",
                    StorageErrorCodes.VersionMismatch);
            }
            JsonElement? global = null;
            if (root.TryGetProperty("global", out var globalValue) && globalValue.ValueKind != JsonValueKind.Null)
            {
                global = globalValue.Clone();
            }
            if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object)
            {
                throw new StorageError($"unit '{descriptor.Name}': tables is not an object", StorageErrorCodes.MalformedMedium);
            }
            var state = new UnitState(descriptor);
            state.Global = global;
            foreach (var table in descriptor.Tables)
            {
                var records = state.Tables[table];
                if (!tables.TryGetProperty(table, out var recordsElement)) continue;
                if (recordsElement.ValueKind != JsonValueKind.Object)
                {
                    throw new StorageError($"unit '{descriptor.Name}': table '{table}' is not an object", StorageErrorCodes.MalformedMedium);
                }
                foreach (var property in recordsElement.EnumerateObject())
                {
                    records[property.Name] = property.Value.Clone();
                }
            }
            return state;
        }
    }

    /// <summary>
    /// Serialize unit state to file content: pretty-printed JSON with a trailing newline, stable
    /// key order from insertion (port of storage-json format.serialize).
    /// </summary>
    private static string Serialize(StorageUnitDescriptor descriptor, UnitState state)
    {
        var tablesNode = new JsonObject();
        foreach (var (table, records) in state.Tables)
        {
            var recordsNode = new JsonObject();
            foreach (var (key, value) in records)
            {
                recordsNode[key] = JsonNode.Parse(value.GetRawText());
            }
            tablesNode[table] = recordsNode;
        }
        var document = new JsonObject
        {
            ["unit"] = new JsonObject
            {
                ["name"] = descriptor.Name,
                ["version"] = descriptor.Version,
            },
            ["global"] = state.Global is JsonElement global ? JsonNode.Parse(global.GetRawText()) : null,
            ["tables"] = tablesNode,
        };
        return JsonSerializer.Serialize(document, Pretty) + "\n";
    }

    /// <summary>
    /// Durably replace <paramref name="path"/> with <paramref name="content"/>: write a
    /// same-directory temp file with exclusive create, flush it to disk, then rename over the
    /// target (atomic replace; last-write-wins — port of storage-json atomic.writeAtomic). The temp
    /// file is removed on any failure.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new StorageError($"cannot publish \"{path}\": invalid path", StorageErrorCodes.IoError);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error)
        {
            TryDeleteQuietly(temp);
            throw new StorageError($"cannot publish \"{path}\": {error.Message}", StorageErrorCodes.IoError, error);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        // The staged temp is owner-only residue; losing it cannot fail a committed write.
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Swallow: cleanup is best-effort by design.
        }
    }

    /// <summary>In-memory authoritative state of one store; the file is its projection.</summary>
    private sealed class UnitState
    {
        public UnitState(StorageUnitDescriptor descriptor)
        {
            Version = descriptor.Version;
            Global = null;
            Tables = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
            foreach (var table in descriptor.Tables)
            {
                Tables[table] = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }
        }

        public int Version { get; }

        public JsonElement? Global { get; set; }

        public Dictionary<string, Dictionary<string, JsonElement>> Tables { get; }
    }

    /// <summary>One opened store: memory is authoritative, every write republishes the whole file atomically.</summary>
    private sealed class JsonStorageUnit : IStorageUnit
    {
        private readonly StorageUnitDescriptor _descriptor;
        private readonly string _path;
        private readonly UnitState _state;
        private readonly Action _onClose;
        private readonly object _gate = new();
        private bool _closed;

        public JsonStorageUnit(StorageUnitDescriptor descriptor, string path, UnitState state, Action onClose)
        {
            _descriptor = descriptor;
            _path = path;
            _state = state;
            _onClose = onClose;
        }

        public JsonElement? Get(string table, string key)
        {
            lock (_gate)
            {
                AssertOpen();
                var records = Records(table);
                return records.TryGetValue(key, out var value) ? value : null;
            }
        }

        public void Set(string table, string key, JsonElement value)
        {
            lock (_gate)
            {
                AssertOpen();
                var records = Records(table);
                var hadKey = records.TryGetValue(key, out var previous);
                records[key] = value.Clone();
                PublishOrRollback(() =>
                {
                    if (hadKey) records[key] = previous;
                    else records.Remove(key);
                });
            }
        }

        public void Delete(string table, string key)
        {
            lock (_gate)
            {
                AssertOpen();
                var records = Records(table);
                if (!records.TryGetValue(key, out var previous)) return;
                records.Remove(key);
                PublishOrRollback(() => records[key] = previous);
            }
        }

        public void SetGlobal(JsonElement value)
        {
            lock (_gate)
            {
                AssertOpen();
                if (!_descriptor.HasGlobal)
                {
                    throw new StorageError(
                        $"unit '{_descriptor.Name}' does not declare a global slot",
                        StorageErrorCodes.NoGlobalSlot);
                }
                var previous = _state.Global;
                _state.Global = value.Clone();
                PublishOrRollback(() => _state.Global = previous);
            }
        }

        public JsonElement? GetGlobal()
        {
            lock (_gate)
            {
                AssertOpen();
                return _state.Global;
            }
        }

        public StorageSnapshot LoadAll()
        {
            lock (_gate)
            {
                AssertOpen();
                var tables = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
                foreach (var (table, records) in _state.Tables)
                {
                    var snapshot = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var (key, value) in records)
                    {
                        snapshot[key] = value.Clone();
                    }
                    tables[table] = snapshot;
                }
                return new StorageSnapshot(tables, _state.Global?.Clone());
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _onClose();
            }
        }

        public void Dispose() => Close();

        private void AssertOpen()
        {
            if (_closed)
            {
                throw new StorageError($"unit '{_descriptor.Name}' is closed", StorageErrorCodes.Closed);
            }
        }

        private Dictionary<string, JsonElement> Records(string table)
        {
            if (!_state.Tables.TryGetValue(table, out var records))
            {
                throw new StorageError(
                    $"unit '{_descriptor.Name}' does not declare table '{table}'",
                    StorageErrorCodes.UndefinedTable);
            }
            return records;
        }

        /// <summary>
        /// Publish the whole file atomically; on a failed publish roll back the in-memory mutation
        /// (memory is authoritative, so a rejected write must not survive in memory or ride along
        /// with the next publish) and rethrow as STORAGE_IO_ERROR.
        /// </summary>
        private void PublishOrRollback(Action rollback)
        {
            try
            {
                WriteAtomic(_path, Serialize(_descriptor, _state));
            }
            catch (StorageError)
            {
                rollback();
                throw;
            }
        }
    }
}
