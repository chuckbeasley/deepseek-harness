namespace Dsh.Terminal.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("open sends and reads output", TerminalTests.Open_SendsAndReadsOutput),
        ("sends retain scrollback across operations", TerminalTests.Send_WithoutSubmit_AppendsNoNewline),
        ("read returns the retained scrollback", TerminalTests.Read_ReturnsTheRetainedScrollback),
        ("unknown backend type fails loud", TerminalTests.UnknownBackendType_FailsLoud),
        ("dispose closes live sessions", TerminalTests.Dispose_ClosesLiveSessions),
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
