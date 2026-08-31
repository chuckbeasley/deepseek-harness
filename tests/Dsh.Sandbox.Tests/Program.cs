namespace Dsh.Sandbox.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("every SandboxMode has its exact wire name", SandboxTypesTests.EveryModeHasItsExactWireName),
        ("enforcement wire names", SandboxTypesTests.EnforcementWireNames),
        ("ShellSandboxInfo JSON round-trips through the shell tool result", SandboxTypesTests.ShellSandboxInfoJsonRoundTripsThroughTheShellToolResult),
        ("the unavailable error carries the verbatim fail-closed text", SandboxTypesTests.TheUnavailableErrorCarriesTheVerbatimFailClosedText),
        ("the strictly-wider ladder", EscalationTests.TheStrictlyWiderLadder),
        ("escalation argument pairing validation", EscalationTests.EscalationArgPairingValidation),
        ("the model-facing markers", EscalationTests.TheModelFacingMarkers),
        ("canonical path and writable roots", EscalationTests.CanonicalPathAndWritableRoots),
        ("the unsandboxed provider registers as sandbox and reports none facts", ProviderTests.RegistersAsSandboxAndReportsNoneFacts),
        ("resolve policy honors an explicit root", ProviderTests.ResolvePolicyHonorsAnExplicitRoot),
        ("sandbox facts round-trip through the shell tool result", ShellToolRoundTripTests.SandboxFactsRoundTripThroughTheShellToolResult),
        ("an unsandboxed result renders no sandbox marker", ShellToolRoundTripTests.AnUnsandboxedResultRendersNoSandboxMarker),
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