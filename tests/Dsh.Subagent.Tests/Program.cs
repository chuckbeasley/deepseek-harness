namespace Dsh.Subagent.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("delegate runs and settles completed", SubagentTests.Delegate_RunsAndSettlesCompleted),
        ("delegate failure settles failed with the error text", SubagentTests.Delegate_FailureSettlesFailedWithTheErrorText),
        ("cancel marks cancelled and settles", SubagentTests.Cancel_MarksCancelledAndSettles),
        ("teardown cancels live delegations", SubagentTests.Teardown_CancelsLiveDelegations),
        ("empty task throws", () => { SubagentTests.EmptyTask_Throws(); return Task.CompletedTask; }),
        ("sdk driver runs a turn end-to-end and mints distinct ids", () => { SdkProviderTests.RunsATurn_EndToEnd_AndMintsDistinctIds(); return Task.CompletedTask; }),
        ("sdk driver runs the child in the configured cwd and sees explicit env", () => { SdkProviderTests.ChildRunsInTheConfiguredCwd_AndSeesExplicitEnv(); return Task.CompletedTask; }),
        ("out-of-process env scrub drops DSH_* and secret names", () => { SdkProviderTests.AmbientScrub_DropsDshAndSecretNames(); return Task.CompletedTask; }),
        ("sdk driver maps the terminal reason table", () => { SdkProviderTests.ReasonMapping_CoversTheTerminalTable(); return Task.CompletedTask; }),
        ("sdk driver fails start on a malformed initialize with protocol facts", () => { SdkProviderTests.MalformedInitialize_FailsStartWithProtocolFacts(); return Task.CompletedTask; }),
        ("sdk driver fails start on an initialize error with protocol facts", () => { SdkProviderTests.InitializeError_FailsStartWithProtocolFacts(); return Task.CompletedTask; }),
        ("sdk driver fails start when the child exits before initialize", () => { SdkProviderTests.ChildExitingBeforeInitialize_FailsStartWithTransportFacts(); return Task.CompletedTask; }),
        ("sdk driver pre-aborted start throws without spawning", () => { SdkProviderTests.PreAbortedStart_ThrowsWithoutSpawning(); return Task.CompletedTask; }),
        ("sdk driver cancel mid-turn settles aborted and reaps the child", () => { SdkProviderTests.CancelMidTurn_SettlesAbortedAndReapsTheChild(); return Task.CompletedTask; }),
        ("sdk driver dispose mid-turn settles aborted and tears the child down", () => { SdkProviderTests.DisposeMidTurn_SettlesAborted_AndTearsTheChildDown(); return Task.CompletedTask; }),
        ("sdk driver child exit mid-turn settles error/transport with partial output", () => { SdkProviderTests.ChildExitingMidTurn_SettlesErrorWithTransportFacts_AndPreservesPartialOutput(); return Task.CompletedTask; }),
        ("sdk driver malformed prompt response settles error/protocol", () => { SdkProviderTests.MalformedPromptResponse_SettlesErrorWithProtocolFacts(); return Task.CompletedTask; }),
        ("registry rejects duplicate and unknown providers", () => { SdkProviderTests.Registry_RejectsDuplicateAndUnknownProviders(); return Task.CompletedTask; }),
        ("sdk config validation fails loud", () => { SdkProviderTests.ConfigValidation_FailsLoud(); return Task.CompletedTask; }),
        ("sdk dispose is idempotent after settlement", () => { SdkProviderTests.DisposeAsync_IsIdempotent_AfterSettlement(); return Task.CompletedTask; }),
        ("diagnostic provider answers the recorded failures", SubagentToolTests.DiagnosticProvider_AnswersTheRecordedFailures),
        ("foreground codex failure renders the recorded text", SubagentToolTests.Foreground_Failure_RendersTheRecordedCodexText),
        ("foreground ACP failure renders the recorded text", SubagentToolTests.AcpForeground_Failure_RendersTheRecordedText),
        ("background run returns the job and settles failed with the detail", SubagentToolTests.Background_ReturnsTheJobAndSettlesFailedWithTheDetail),
        ("published failure throws the recorded error", () => { SubagentToolTests.PublishedFailure_ThrowsTheRecordedError(); return Task.CompletedTask; }),
    };

    public static int Main(string[] args)
    {
        if (args.Any(argument => argument == "--fake-sdk-child"))
        {
            return FakeSdkChild.Run();
        }
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
