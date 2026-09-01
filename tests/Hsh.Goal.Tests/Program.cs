namespace Harness.Goal.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("an empty log yields no current goal", GoalTests.EmptyLog_YieldsNoCurrentGoal),
        ("goal/write events fold last-write-wins into the current state", GoalTests.GoalWriteEvents_FoldLastWriteWins),
        ("the state updates live on session/event", GoalTests.StateUpdatesLiveOnSessionEvent),
        ("the goal event round-trips the JSONL (snapshot and clear)", GoalTests.GoalWriteEvent_RoundTripsTheJsonl),
        ("goal_write executes through the tool runtime and appends the durable event", GoalTests.GoalWriteTool_ExecutesThroughToolRuntime_AndAppendsTheDurableEvent),
        ("goal service rejects a blank objective and an invalid round cap", GoalTests.Create_RejectsBlankObjectiveAndInvalidRoundCap),
        ("goal service rejects a second non-complete goal", GoalTests.Create_RejectsAnExistingNonCompleteGoal),
        ("goal service rejects stale revisions and empty edits", GoalTests.Edit_RejectsStaleRevisionsAndEmptyEdits),
        ("goal phase transitions and clear behave", GoalTests.PhaseTransitions_AndClear_Behave),
        ("goal block requires a valid reason", GoalTests.Block_RequiresAValidReason),
        ("disarm removes process-local authority without changing the goal", GoalTests.Disarm_RemovesProcessLocalAuthorityWithoutChangingTheGoal),
        ("the service registers as the goal service", GoalTests.RegistersAsTheGoalService),
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
