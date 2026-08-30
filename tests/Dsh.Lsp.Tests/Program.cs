namespace Dsh.Lsp.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("transport round-trips a framed message", LspTests.Transport_RoundTripsAFramedMessage),
        ("client request gets its response", LspTests.Client_RequestGetsItsResponse),
        ("client dispatches notifications", LspTests.Client_DispatchesNotifications),
        ("missing Content-Length fails loud", LspTests.Transport_MissingLengthHeaderFailsLoud),
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
