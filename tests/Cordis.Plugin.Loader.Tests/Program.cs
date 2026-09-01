namespace Harness.Cordis.Plugin.Loader.Tests;

/// <summary>
/// Zero-dependency console test runner for the Phase 1 loader port. The host sandbox
/// blocks <c>dotnet build</c>/<c>dotnet test</c> (MSBuild cannot spawn the C#
/// compiler with captured output), so tests run as a plain console app that
/// exits non-zero on any assertion failure.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Harness.Cordis.Plugin.Loader Phase 1 - console assertions");
        Console.WriteLine();

        Run("Ordered load: rows register services in row order", LoaderTests.OrderedLoad_RegistersServicesInRowOrder);
        Run("Dependency gate: pending until the provider appears", LoaderTests.DependencyGated_PendingUntilProviderAppears);
        Run("Replace update: swap swaps the service", LoaderTests.ReplaceUpdate_SwapsService);
        Run("Failing update: restores the previous row", LoaderTests.FailingUpdate_RestoresPreviousRow);
        Run("Disposal unwind: rows and services are removed", LoaderTests.DisposalUnwind_DisposesRowsAndServices);
        Run("Group reconcile: failed candidate rolls the group back", LoaderTests.GroupReconciliation_RollsBackFailedCandidate);
        Run("Config-only update: notifies the updatable plugin", LoaderTests.ConfigOnlyUpdate_NotifiesUpdatablePlugin);
        Run("Disabled row: does not mount", LoaderTests.DisabledRow_DoesNotMount);
        Run("Duplicate row id: rejected", LoaderTests.DuplicateRowId_IsRejected);
        Run("Group row: composes a nested tree", LoaderTests.GroupRow_ComposesNestedTree);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (_failed > 0)
        {
            foreach (var failure in Failures)
            {
                Console.WriteLine("  FAILED: " + failure);
            }
            return 1;
        }
        return 0;
    }

    private static void Run(string name, Func<Task> test)
    {
        try
        {
            test().GetAwaiter().GetResult();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (AssertionException ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }
}
