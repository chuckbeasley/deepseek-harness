namespace Dsh.Sdk.Server.Tests;

/// <summary>Zero-dependency console test runner for the SDK runtime server.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("server: initialize returns the identity and records the route", SdkServerTests.Initialize_ReturnsTheServerIdentity_AndRecordsTheRoute),
        ("server: initialize rejects malformed parameters", SdkServerTests.Initialize_RejectsMalformedParameters),
        ("server: the official route mounts the fallback adapter", SdkServerTests.DeepseekOfficial_MountsTheFallbackAdapter),
        ("server: a prompt before the handshake fails", SdkServerTests.Prompt_WithoutInitialize_Fails),
        ("server: a turn runs and streams the live notifications", SdkServerTests.ATurn_RunsAndStreamsTheLiveNotifications),
        ("server: image blocks are rejected until admission is ported", SdkServerTests.ImageBlocks_AreRejectedUntilAdmissionIsPorted),
        ("server: shutdown disposes sessions and further prompts fail", SdkServerTests.Shutdown_DisposesSessions_AndFurtherPromptsFail),
        ("server: an unknown method answers the transport error", SdkServerTests.UnknownMethod_AnswersTheTransportError),
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
                if (!watchdog.Wait(TimeSpan.FromSeconds(45)))
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


