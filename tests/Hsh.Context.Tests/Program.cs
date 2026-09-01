namespace Harness.Context.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("every contributor's text appears in the assembly", ContextTests.EachContributorText_AppearsInAssembly),
        ("empty contributors contribute nothing", ContextTests.EmptyContributors_ContributeNothing),
        ("file references resolve within the root and fail loud outside", ContextTests.FileReferences_ResolveWithinRoot_AndFailLoudOutside),
        ("registration order is preserved and disposers unregister", ContextTests.RegistrationOrder_IsPreserved_AndDisposerUnregisters),
        ("the time contributor uses the injected clock", ContextTests.TimeContext_UsesInjectedClock),
        ("session references resolve and fail loud on self and unknown", ContextTests.SessionReference_Resolves_AndFailsLoudOnSelfAndUnknown),
        ("the provider registers as the context service", ContextTests.RegistersAsTheContextService),
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
