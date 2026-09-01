namespace Harness.Subprocess.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("collect reads stdout and the exit code", SubprocessTests.Collect_ReadsStdoutAndExitCode),
        ("non-zero exit codes propagate", SubprocessTests.NonZero_ExitCodePropagates),
        ("env merges explicit entries and scrubs HSH_ facts", SubprocessTests.Env_MergesExplicitAndScrubsManagedFacts),
        ("collect keeps the bounded tail and spills the full stream", SubprocessTests.Collect_KeepsTheBoundedTailAndSpillsTheFullStream),
        ("terminate kills the tree and settles done", SubprocessTests.Terminate_KillsTheTreeAndSettlesDone),
        ("stdin batch writes then closes", SubprocessTests.Stdin_BatchWritesThenCloses),
        ("waitForExit honors cancellation", SubprocessTests.WaitForExit_HonorsCancellation),
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
