namespace Harness.Tui.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("transcript folds messages and completes tool rows", ModelTests.Transcript_FoldsMessagesAndCompletesToolRows),
        ("todo panel is last-write-wins and replays", ModelTests.Todo_LastWriteWins_AndResetReplays),
        ("pending approval decides once", ModelTests.PendingApproval_DecidesOnce),
        ("fake-driver smoke exits 0", ModelTests.TuiApp_SmokeRunsHeadlessly),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                run().GetAwaiter().GetResult();
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
