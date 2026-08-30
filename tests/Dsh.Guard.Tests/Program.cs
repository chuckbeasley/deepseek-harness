namespace Dsh.Guard.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    /// <summary>Wrap a synchronous suite as a task suite.</summary>
    private static Func<Task> Sync(Action run)
        => () =>
        {
            run();
            return Task.CompletedTask;
        };

    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("repeat reminder trips exactly at the thresholds, gentle then detailed", RepeatToolReminderTests.TripsExactlyAtTheThreshold),
        ("repeat reminder stays silent before the threshold", RepeatToolReminderTests.NoReminderBeforeTheThreshold),
        ("repeat reminder is durable in the persisted log", RepeatToolReminderTests.ReminderIsDurable),
        ("distinct tools do not trip the repeat chain", RepeatToolReminderTests.DistinctToolsDoNotTrip),
        ("custom thresholds are normalized ascending", RepeatToolReminderTests.CustomThresholdsAreNormalizedAscending),
        ("excluded tools are transparent to the chain", RepeatToolReminderTests.ExcludedToolsAreTransparent),
        ("include patterns track only matching tools", RepeatToolReminderTests.IncludePatternsTrackOnlyMatchingTools),
        ("the detailed reminder caps the arguments preview", RepeatToolReminderTests.DetailedReminderCapsTheArgumentsPreview),
        ("a user prompt resets the chain", RepeatToolReminderTests.UserMessageResetsTheChain),
        ("chains are keyed per agent", RepeatToolReminderTests.ChainsAreKeyedPerAgent),
        ("a fresh agent starts a fresh chain", RepeatToolReminderTests.FreshAgentStartsWithAFreshChain),
        ("repeat-reminder config validation fails loud", Sync(RepeatToolReminderTests.ConfigValidationFailsLoud)),
        ("timeout policy delegates unconfigured tools unchanged", Sync(TimeoutPolicyTests.DelegatesUnconfiguredToolsUnchanged)),
        ("timeout policy keeps a fast budgeted tool's own result", Sync(TimeoutPolicyTests.FastBudgetedToolKeepsItsOwnResult)),
        ("timeout policy substitutes TOOL_TIMEOUT when the budget expires", Sync(TimeoutPolicyTests.SlowToolIsReplacedWithTheTimeoutResult)),
        ("timeout policy applies the default budget to unlisted tools", Sync(TimeoutPolicyTests.DefaultTimeoutAppliesToUnlistedTools)),
        ("timeout policy clamps every effective budget to the cap", Sync(TimeoutPolicyTests.CapClampsTheEffectiveBudget)),
        ("TimeoutMsFor resolves budgets and the reminder guard arms none", Sync(TimeoutPolicyTests.TimeoutMsForResolvesBudgets)),
        ("the reminder guard arms no timeout", Sync(TimeoutPolicyTests.ReminderGuardArmsNoTimeout)),
        ("timeout-policy config validation fails loud", Sync(TimeoutPolicyTests.ConfigValidationFailsLoud)),
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
