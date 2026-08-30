namespace Dsh.Todo.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("service replaces the whole list and computes counts", TodoTests.Service_ReplacesWholeList_AndComputesCounts),
        ("service rejects invalid lists", TodoTests.Service_RejectsInvalidLists),
        ("service allows parallel in_progress when enabled", TodoTests.Service_AllowsParallelInProgress_WhenEnabled),
        ("tool executes through the runtime and appends the durable event", () => TodoTests.Tool_ExecutesThroughTheToolRuntime_AndAppendsTheDurableEvent().GetAwaiter().GetResult()),
        ("tool requires the mounted service", TodoTests.Tool_RequiresTheMountedService),
        ("event round-trips the JSONL serializer", TodoTests.Event_RoundTripsTheJsonlSerializer),
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
