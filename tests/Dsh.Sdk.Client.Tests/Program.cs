namespace Dsh.Sdk.Client.Tests;

/// <summary>Zero-dependency console test runner for the SDK client.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("launch: the defaults resolve to the sdk profile", LaunchTests.ResolveLaunch_AppliesTheDefaults),
        ("launch: the overrides resolve caller-relative inputs", LaunchTests.ResolveLaunch_AppliesTheOverrides),
        ("semantics: normalizeInput turns text into one text block", SemanticsTests.NormalizeInput_TurnsTextIntoOneTextBlock),
        ("semantics: finalResponse selects the last assistant message text", SemanticsTests.FinalResponse_SelectsTheLastAssistantMessageText),
        ("semantics: the inbox receipt matches the durable user message", SemanticsTests.IsInboxReceipt_MatchesTheDurableUserMessage),
        ("semantics: the event envelope validation rejects malformed variants", SemanticsTests.ValidatedSessionEvent_ValidatesTheReadVariants),
        ("semantics: the lineage map tracks descendant chains", SemanticsTests.SessionLineage_TracksDescendantChains),
        ("client: the handshake returns the wire identity and unknown methods answer -32603", ClientE2eTests.Initialize_ReturnsTheWireIdentity_AndUnknownMethodsAnswerTheProtocolError),
        ("client: a turn streams notifications, settles idle, and scopes to the session tree", ClientE2eTests.ATurn_StreamsNotifications_SettlesIdle_AndScopesToTheSessionTree),
        ("client: a request timeout abandons the call and close quiesces the runtime", ClientE2eTests.ARequestTimeout_AbandonsTheCall_AndClose_QuiescesTheRuntime),
        ("client: DeepSeekHarness runs a turn and returns the owned interval", ClientE2eTests.DeepSeekHarness_RunsATurn_AndReturnsTheOwnedInterval),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                var watchdog = Task.Run(run);
                if (!watchdog.Wait(TimeSpan.FromSeconds(90)))
                {
                    throw new AssertionException("TIMEOUT (test hung)");
                }
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
