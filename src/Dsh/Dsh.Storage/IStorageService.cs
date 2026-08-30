using System.Text.Json;

namespace Dsh.Storage;

/// <summary>
/// The storage capability Service Definition (port of the TS <c>Storage</c> hub plus the
/// <c>KvFacet</c> contract of packages/storage/storage). A consumer opens one scoped store by
/// descriptor — name (scope), format revision, declared tables, and an optional global slot — and
/// runs get/set/delete against the returned <see cref="IStorageUnit"/>. The hub itself performs no
/// IO; the provider owns the medium.
///
/// This C# seam fuses the TS hub/registry and the single JSON backend into one provider; the
/// multi-backend registry and data-form mounting (storage-sqlite, storage-domain) are deferred and
/// named in the provider docs. Values are opaque JSON: pass <see cref="JsonSerializer.SerializeToElement"/>
/// output to Set* and read JsonElement-typed values back.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Open one scoped store, creating it when the medium holds no trace of it yet (materialization
    /// defers to the first write; the empty shape is served immediately). A stored revision that
    /// differs from <paramref name="descriptor"/>.Version rejects with STORAGE_VERSION_MISMATCH; a
    /// medium that cannot be parsed as this store rejects with STORAGE_MALFORMED_MEDIUM. Opening the
    /// same store name twice without closing is a caller bug and rejects with STORAGE_ALREADY_OPEN.
    /// </summary>
    /// <param name="descriptor">static identity and shape of the store to open.</param>
    /// <returns>the opened store.</returns>
    IStorageUnit Open(StorageUnitDescriptor descriptor);

    /// <summary>
    /// Close the backend: every open store is closed (draining in-flight publishes) and the medium
    /// is released. Idempotent; subsequent opens reject with STORAGE_CLOSED.
    /// </summary>
    void Close();
}

/// <summary>
/// One opened scoped store. Values are opaque JSON to this layer — no schema, no events, no domain
/// meaning. Each single call is atomic on the medium and durable once it returns. Any call after
/// <see cref="Close"/> rejects with STORAGE_CLOSED. Writes serialize within the process (a
/// superset of the TS contract, which leaves ordering to the caller); a failed publish rolls the
/// in-memory state back so a rejected write never survives in memory.
/// </summary>
public interface IStorageUnit : IDisposable
{
    /// <summary>Read one record, or <c>null</c> when the key is absent.</summary>
    /// <param name="table">a declared table name.</param>
    /// <param name="key">the record key.</param>
    JsonElement? Get(string table, string key);

    /// <summary>Upsert one record durably; an existing key is replaced. Fails loud on a failed publish.</summary>
    /// <param name="table">a declared table name.</param>
    /// <param name="key">the record key.</param>
    /// <param name="value">opaque JSON to store.</param>
    void Set(string table, string key, JsonElement value);

    /// <summary>Delete one record durably. Idempotent: a missing key is a no-op.</summary>
    /// <param name="table">a declared table name.</param>
    /// <param name="key">the record key.</param>
    void Delete(string table, string key);

    /// <summary>Write the global singleton durably; only valid when the descriptor declared <c>HasGlobal</c>.</summary>
    /// <param name="value">opaque JSON to store.</param>
    void SetGlobal(JsonElement value);

    /// <summary>Read the global singleton, or <c>null</c> when never written or not declared.</summary>
    JsonElement? GetGlobal();

    /// <summary>Read the full current snapshot: every declared table's records plus the global singleton.</summary>
    StorageSnapshot LoadAll();

    /// <summary>Drain in-flight publishes and release the store. Idempotent.</summary>
    void Close();
}
