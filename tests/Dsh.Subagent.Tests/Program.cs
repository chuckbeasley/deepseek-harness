namespace Dsh.Subagent.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("delegate runs and settles completed", SubagentTests.Delegate_RunsAndSettlesCompleted),
        ("delegate failure settles failed with the error text", SubagentTests.Delegate_FailureSettlesFailedWithTheErrorText),
        ("cancel marks cancelled and settles", SubagentTests.Cancel_MarksCancelledAndSettles),
        ("teardown cancels live delegations", SubagentTests.Teardown_CancelsLiveDelegations),
        ("empty task throws", () => { SubagentTests.EmptyTask_Throws(); return Task.CompletedTask; }),
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
