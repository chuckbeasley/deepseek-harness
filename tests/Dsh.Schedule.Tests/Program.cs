namespace Harness.Schedule.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites =
    {
        Test("the provider registers as the schedule service and requires the timer", ScheduleTests.RegistersAsTheScheduleService_AndRequiresTheTimer),
        Test("a missing timer service fails loud", () => { ScheduleTests.MissingTimerService_FailsLoud(); return Task.CompletedTask; }),
        Test("a once task fires on schedule and then leaves the list", ScheduleTests.RegisterOnce_FiresOnSchedule_ThenLeavesTheList),
        Test("cancelling a once task prevents its fire and removes it", ScheduleTests.RegisterOnce_Cancel_PreventsFireAndRemovesTheTask),
        Test("a recurring task fires repeatedly until cancelled", ScheduleTests.RegisterRecurring_FiresRepeatedly_UntilCancelled),
        Test("the list reflects registration and cancellation", () => { ScheduleTests.List_ReflectsRegistration(); return Task.CompletedTask; }),
        Test("task failures are contained and logged and the schedule survives", ScheduleTests.CallbackFailure_IsContainedAndLogged_AndTheScheduleSurvives),
        Test("registration rejects empty names and non-positive delays", () => { ScheduleTests.Registration_RejectsEmptyNamesAndNonPositiveDelays(); return Task.CompletedTask; }),
    };

    public static async Task<int> Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                await run();
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

    private static (string Name, Func<Task> Run) Test(string name, Func<Task> run) => (name, run);
}
