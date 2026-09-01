namespace Harness.Plan.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("an empty log yields an empty plan", PlanTests.EmptyLog_YieldsAnEmptyPlan),
        ("plan/write events fold last-write-wins into the current state", PlanTests.PlanWriteEvents_FoldLastWriteWins),
        ("the state updates live on session/event", PlanTests.StateUpdatesLiveOnSessionEvent),
        ("the plan event round-trips the JSONL", PlanTests.PlanWriteEvent_RoundTripsTheJsonl),
        ("plan_write executes through the tool runtime and appends the durable event", PlanTests.PlanWriteTool_ExecutesThroughToolRuntime_AndAppendsTheDurableEvent),
        ("plan_write rejects empty, duplicate, and multi-active input", PlanTests.Write_RejectsEmptyContentDuplicatesAndMultipleInProgress),
        ("the service registers as the plan service", PlanTests.RegistersAsThePlanService),
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
