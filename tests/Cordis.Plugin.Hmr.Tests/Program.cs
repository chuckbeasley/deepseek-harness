namespace Cordis.Plugin.Hmr.Tests;

/// <summary>
/// Zero-dependency console test runner for the Phase 1 HMR port. The host sandbox blocks
/// <c>dotnet build</c>/<c>dotnet test</c> (MSBuild cannot spawn the C# compiler with captured
/// output), so tests run as a plain console app that exits non-zero on any assertion failure.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Cordis.Plugin.Hmr Phase 1 - console assertions");
        Console.WriteLine();

        RunAsync("Register: watches its file and refreshes on change", HmrTests.RegisterWatchesFileAndRefreshes);
        RunAsync("Missing parent: watch becomes live once the file appears", HmrTests.MissingParentBecomesWatchable);
        RunAsync("Broken config: failure logs and later refreshes continue", HmrTests.BrokenRefreshLogsAndContinues);
        RunAsync("Dispose: stops further refreshes", HmrTests.DisposingStopsFurtherRefreshes);
        Run("Duplicate registration: rejected", HmrTests.DuplicateRegistrationThrows);

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

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
    }

    private static void RunAsync(string name, Func<Task> test)
    {
        try
        {
            test().GetAwaiter().GetResult();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
