namespace Dsh.Spill.Tests;

/// <summary>Zero-dependency console test runner for the spill capability seam.</summary>
public static class Program
{
    private static readonly (string Name, Action<Harness> Run)[] Suites = new (string, Action<Harness>)[]
    {
        ("claim/list/release round-trip", SpillTests.ClaimListReleaseRoundTrip),
        ("claimed bytes are the UTF-8 length", SpillTests.ClaimBytesAreUtf8Length),
        ("hostile claim names are traversal-safe", SpillTests.ClaimNameIsTraversalSafe),
        ("claims land in session-scoped directories", SpillTests.ClaimUsesSessionScopedDirectory),
        ("register admits a pre-existing spill path", SpillTests.RegisterExistingSpillPath),
        ("register outside the root fails loud", SpillTests.RegisterOutsideRootFailsLoud),
        ("register a missing path fails loud", SpillTests.RegisterMissingPathFailsLoud),
        ("duplicate register fails loud", SpillTests.DuplicateRegisterFailsLoud),
        ("release tolerates a vanished file", SpillTests.ReleaseToleratesMissingFile),
        ("cleanup on dispose deletes registered files", SpillTests.CleanupOnDisposeDeletesRegisteredFiles),
        ("age sweep removes expired files", SpillTests.CleanupSweepRemovesExpiredFiles),
        ("cleanup leaves unrelated entries", SpillTests.CleanupLeavesUnrelatedEntries),
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
