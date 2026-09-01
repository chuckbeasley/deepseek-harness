namespace Harness.Storage.Tests;

/// <summary>Zero-dependency console test runner for the storage capability seam.</summary>
public static class Program
{
    private static readonly (string Name, Action<Harness> Run)[] Suites = new (string, Action<Harness>)[]
    {
        ("get/set round-trips across provider instances", StorageTests.GetSetRoundTripAcrossProviders),
        ("load-all snapshot and durable delete", StorageTests.LoadAllSnapshotAndDelete),
        ("revision conflict fails loud", StorageTests.RevisionConflictFailsLoud),
        ("corrupt file fails loud", StorageTests.CorruptFileFailsLoud),
        ("foreign unit header fails loud", StorageTests.ForeignUnitHeaderFailsLoud),
        ("invalid unit name fails loud", StorageTests.InvalidUnitNameFailsLoud),
        ("double open fails loud", StorageTests.DoubleOpenFailsLoud),
        ("closed unit rejects operations", StorageTests.ClosedUnitRejectsOperations),
        ("closed backend rejects opens", StorageTests.ProviderCloseRejectsOpens),
        ("undeclared table and global slot guard", StorageTests.UndeclaredTableAndGlobalSlotGuard),
        ("failed publish rolls back memory", StorageTests.FailedPublishRollsBackMemory),
        ("empty unit serves the empty shape", StorageTests.EmptyUnitServesEmptyShape),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                using var harness = Harness.Create();
                run(harness);
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL  {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}
