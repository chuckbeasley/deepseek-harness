namespace Harness.Shell.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("run echoes stdout with exit zero", ShellTests.Run_EchoesStdoutWithExitZero),
        ("resolve fills and caps defaults", ShellTests.Resolve_FillsAndCapsDefaults),
        ("run honors the workdir override", ShellTests.Run_WorkdirOverrideApplies),
        ("timeout kills and classifies", ShellTests.Run_TimeoutKillsAndClassifies),
        ("caller cancellation classifies aborted", ShellTests.Run_CallerCancellationClassifiesAborted),
        ("bash tool executes through the tool runtime", ShellTests.BashTool_ExecutesThroughTheToolRuntime),
        ("bash tool renders the non-zero exit marker", ShellTests.BashTool_RendersNonZeroExitMarker),
        ("bash tool rejects invalid arguments", ShellTests.BashTool_RejectsInvalidArguments),
        ("a missing shell fails loud", ShellTests.Run_MissingShellFailsLoud),
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
