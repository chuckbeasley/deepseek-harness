using System.Text.Json;
using Cordis.Core;
using Dsh.Code;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Code.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
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

    private static readonly (string Name, Func<Task> Run)[] Suites =
    {
        ("the recorded program executes and renders its return value", RunCodeToolTests.RecordedProgram_ExecutesAndRenders),
        ("console output prefixes the return value", RunCodeToolTests.ConsoleOutput_PrefixesTheReturnValue),
        ("dispatched tool calls record the code-dispatch pairs", RunCodeToolTests.DispatchedCalls_RecordTheDispatchPairs),
        ("a non-string return renders as two-space JSON", RunCodeToolTests.ObjectReturn_RendersAsTwoSpaceJson),
    };
}