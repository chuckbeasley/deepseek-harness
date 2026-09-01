namespace Harness.Compaction.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("budgeted trim shadows everything above the retained tail", CompactionTests.BudgetedTrim_ShadowsEverythingAboveTheRetainedTail),
        ("tool-pairing keeps a call and its result together", CompactionTests.ToolPairing_KeepsCallAndResultTogether),
        ("identical sessions produce deterministic output", CompactionTests.DeterministicOutput_ForIdenticalSessions),
        ("empty or fully retained logs compact nothing", CompactionTests.NoCompactableRange_ForEmptyOrFullyRetainedLogs),
        ("budget validation fails loud", CompactionTests.BudgetValidation_FailsLoud),
        ("an unmatched compaction/start holds the busy lock", CompactionTests.BusyLock_RejectsConcurrentCompaction),
        ("the compaction events round-trip the JSONL", CompactionTests.CompactionEvents_RoundTripTheJsonl),
        ("the provider registers as the compaction service", CompactionTests.RegistersAsTheCompactionService),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                run();
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
