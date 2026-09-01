using System.Text.Json;

namespace Harness.Storage;

/// <summary>
/// Static identity and shape of one scoped key-value store (port of the TS <c>KvUnitDescriptor</c>).
/// <c>Name</c> is the scope: it must match <c>[a-z][a-z0-9_]*</c> and is also the file-name segment.
/// <c>Version</c> is the store's format revision, stamped on the medium at first write; opening a
/// store whose stored revision differs rejects with STORAGE_VERSION_MISMATCH.
/// </summary>
public sealed record StorageUnitDescriptor(string Name, int Version, IReadOnlyList<string> Tables, bool HasGlobal = false);

/// <summary>
/// Full current snapshot of one store: every declared table's records keyed by table name, plus
/// the global singleton (<c>null</c> when never written or not declared). Values are opaque JSON.
/// </summary>
public sealed record StorageSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Tables, JsonElement? Global);
