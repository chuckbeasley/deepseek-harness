namespace Harness.AgentLoop.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Harness, Task> Run)[] Suites = new (string, Func<Harness, Task>)[]
    {
        ("full turn: tool call then text, persisted and replayed", FullTurnTests.RunAsync),
        ("pre-step reject blocks the turn without a model call", PreStepTests.RunAsync),
        ("cancellation logs an aborted turn and quiesces", CancellationTests.RunAsync),
        ("resume continues turn numbering; invariant rejects divergence", ResumeAndInvariantTests.RunAsync),
        ("runtime context projects one snapshot per change", RuntimeContextTests.RunAsync),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                RunSuite(run).GetAwaiter().GetResult();
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

    private static async Task RunSuite(Func<Harness, Task> run)
    {
        var harness = Harness.Create();
        await using (harness)
        {
            await run(harness);
        }
    }
}
