namespace Dsh.Acp.Tests;

/// <summary>Zero-dependency console test runner for the ACP server.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("codec: the stop-reason vocabulary maps the lifecycle", AcpCodecTests.TurnEndToStopReason_MapsTheVocabulary),
        ("model control: the fixed route advertises the option state", AcpCodecTests.ModelControl_AdvertisesTheFixedRoute),
        ("model control: no route advertises nothing", AcpCodecTests.ModelControl_WithoutARoute_AdvertisesNothing),
        ("server: initialize returns the identity and capabilities", AcpServerTests.Initialize_ReturnsTheIdentityAndCapabilities),
        ("server: session/new validates and creates the session", AcpServerTests.NewSession_ValidatesAndCreatesTheSession),
        ("server: a turn streams the committed updates to end_turn", AcpServerTests.ATurn_RunsAndStreamsTheCommittedUpdates),
        ("server: the approval bridge asks one-shot decisions", AcpServerTests.TheApprovalBridge_AsksOneShotDecisions),
        ("server: session/cancel stops the active prompt", AcpServerTests.Cancel_StopsTheActivePrompt),
        ("server: session/close disposes and further prompts fail", AcpServerTests.CloseSession_DisposesTheAgent_AndFurtherPromptsFail),
        ("server: session/list pages the persisted sessions", AcpServerTests.ListSessions_PagesThePersistedSessions),
        ("server: session/resume restores a persisted session", AcpServerTests.ResumeSession_RestoresAPersistedSession),
        ("server: the reductions refuse images and unknown blocks", AcpServerTests.TheReductions_RefuseImagesAndUnknownBlocks),
        ("profile: the acp profile serves a real client over stdio", AcpServerTests.TheAcpProfile_ServesARealClientOverStdio),
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
