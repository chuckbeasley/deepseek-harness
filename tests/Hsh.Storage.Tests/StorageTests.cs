using System.Text.Json;

namespace Harness.Storage.Tests;

/// <summary>Behavior tests for the storage capability seam (ports the storage-json contract suite).</summary>
public static class StorageTests
{
    /// <summary>Set records and the global slot, close, reopen on a fresh provider over the same root, and read them back.</summary>
    public static void GetSetRoundTripAcrossProviders(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
        unit.Set("documents", "a", JsonSerializer.SerializeToElement(new { hello = "world", n = 42 }));
        unit.Set("documents", "b", JsonSerializer.SerializeToElement(new[] { 1, 2, 3 }));
        unit.SetGlobal(JsonSerializer.SerializeToElement("g"));
        unit.Close();

        var ctx2 = new Context();
        using (var storage2 = new JsonFileStorageProvider(ctx2, new JsonFileStorageConfig(h.Root)))
        {
            var unit2 = storage2.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
            var a = unit2.Get("documents", "a");
            Assert.NotNull(a);
            Assert.Equal("world", a!.Value.GetProperty("hello").GetString());
            Assert.Equal(42, a.Value.GetProperty("n").GetInt32());
            Assert.Equal(3, unit2.Get("documents", "b")!.Value.GetArrayLength());
            Assert.Equal("g", unit2.GetGlobal()!.Value.GetString());
            var text = File.ReadAllText(Path.Combine(h.Root, "notes.json"));
            Assert.True(text.EndsWith("\n"), "unit file carries a trailing newline");
            Assert.True(text.Contains("\"unit\""), "unit file carries the unit header");
        }
    }

    /// <summary>Load-all returns every declared table plus the global slot; delete is durable and idempotent.</summary>
    public static void LoadAllSnapshotAndDelete(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("state", 1, new[] { "kv" }, HasGlobal: true));
        unit.Set("kv", "x", JsonSerializer.SerializeToElement("one"));
        unit.SetGlobal(JsonSerializer.SerializeToElement(true));

        var snapshot = unit.LoadAll();
        Assert.Equal(1, snapshot.Tables.Count);
        Assert.True(snapshot.Tables.ContainsKey("kv"));
        Assert.Equal("one", snapshot.Tables["kv"]["x"].GetString());
        Assert.True(snapshot.Global!.Value.GetBoolean());

        unit.Delete("kv", "x");
        Assert.Null(unit.Get("kv", "x"));
        unit.Delete("kv", "x"); // idempotent
        var after = unit.LoadAll();
        Assert.Equal(0, after.Tables["kv"].Count);
        Assert.True(after.Global!.Value.GetBoolean(), "delete leaves the global slot untouched");
    }

    /// <summary>Opening a store whose stored revision differs from the descriptor fails loud.</summary>
    public static void RevisionConflictFailsLoud(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
        unit.Set("documents", "a", JsonSerializer.SerializeToElement(1));
        unit.Close();

        var error = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("notes", 2, new[] { "documents" })));
        Assert.Equal(StorageErrorCodes.VersionMismatch, error.Code);
    }

    /// <summary>A unit file that is not valid JSON fails loud with STORAGE_MALFORMED_MEDIUM.</summary>
    public static void CorruptFileFailsLoud(Harness h)
    {
        File.WriteAllText(Path.Combine(h.Root, "notes.json"), "{ not json !!!");
        var error = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" })));
        Assert.Equal(StorageErrorCodes.MalformedMedium, error.Code);
    }

    /// <summary>A unit file whose header names a different unit fails loud.</summary>
    public static void ForeignUnitHeaderFailsLoud(Harness h)
    {
        File.WriteAllText(Path.Combine(h.Root, "notes.json"), "{ \"unit\": { \"name\": \"other\", \"version\": 1 }, \"global\": null, \"tables\": {} }\n");
        var error = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" })));
        Assert.Equal(StorageErrorCodes.MalformedMedium, error.Code);
    }

    /// <summary>Unit and table names that do not match <c>[a-z][a-z0-9_]*</c> are rejected.</summary>
    public static void InvalidUnitNameFailsLoud(Harness h)
    {
        var error = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("bad name", 1, Array.Empty<string>())));
        Assert.Equal(StorageErrorCodes.InvalidName, error.Code);
        var error2 = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("ok", 1, new[] { "Bad" })));
        Assert.Equal(StorageErrorCodes.InvalidName, error2.Code);
    }

    /// <summary>Opening the same unit twice without closing is a caller bug and fails loud.</summary>
    public static void DoubleOpenFailsLoud(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
        var error = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" })));
        Assert.Equal(StorageErrorCodes.AlreadyOpen, error.Code);
        unit.Close();
        var again = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }));
        Assert.NotNull(again);
    }

    /// <summary>A closed unit rejects every operation with STORAGE_CLOSED.</summary>
    public static void ClosedUnitRejectsOperations(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
        unit.Close();
        var error = Assert.Throws<StorageError>(() => unit.Set("documents", "a", JsonSerializer.SerializeToElement(1)));
        Assert.Equal(StorageErrorCodes.Closed, error.Code);
    }

    /// <summary>A closed backend rejects opens with STORAGE_CLOSED.</summary>
    public static void ProviderCloseRejectsOpens(Harness h)
    {
        h.Storage.Close();
        var error = Assert.Throws<StorageError>(() => h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" })));
        Assert.Equal(StorageErrorCodes.Closed, error.Code);
    }

    /// <summary>An undeclared table and an undeclared global slot both fail loud.</summary>
    public static void UndeclaredTableAndGlobalSlotGuard(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }));
        var error = Assert.Throws<StorageError>(() => unit.Set("missing", "a", JsonSerializer.SerializeToElement(1)));
        Assert.Equal(StorageErrorCodes.UndefinedTable, error.Code);
        var error2 = Assert.Throws<StorageError>(() => unit.SetGlobal(JsonSerializer.SerializeToElement(1)));
        Assert.Equal(StorageErrorCodes.NoGlobalSlot, error2.Code);
    }

    /// <summary>A failed publish rolls the in-memory state back so the rejected write never survives.</summary>
    public static void FailedPublishRollsBackMemory(Harness h)
    {
        // Block the unit file path with a directory so the atomic rename fails.
        Directory.CreateDirectory(Path.Combine(h.Root, "notes.json"));
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
        var error = Assert.Throws<StorageError>(() => unit.Set("documents", "a", JsonSerializer.SerializeToElement(1)));
        Assert.Equal(StorageErrorCodes.IoError, error.Code);
        Assert.Null(unit.Get("documents", "a"), "a failed publish must not survive in memory");
    }

    /// <summary>A never-written store serves the empty shape and materializes no file until the first write.</summary>
    public static void EmptyUnitServesEmptyShape(Harness h)
    {
        var unit = h.Storage.Open(new StorageUnitDescriptor("notes", 1, new[] { "documents" }, HasGlobal: true));
        var snapshot = unit.LoadAll();
        Assert.Equal(1, snapshot.Tables.Count);
        Assert.Equal(0, snapshot.Tables["documents"].Count);
        Assert.Null(snapshot.Global);
        Assert.Null(unit.Get("documents", "a"));
        Assert.False(File.Exists(Path.Combine(h.Root, "notes.json")), "materialization defers to the first write");
    }
}
