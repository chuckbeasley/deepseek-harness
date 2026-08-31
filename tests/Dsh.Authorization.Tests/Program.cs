namespace Dsh.Authorization.Tests;

/// <summary>
/// Zero-dependency console test runner for the authorization capability seam. The host sandbox
/// blocks dotnet build/dotnet test (MSBuild cannot spawn the C# compiler with captured output), so
/// tests run as a plain console app that exits non-zero on any assertion failure. All file-backed
/// tests use disposable temp directories; no process environment is mutated.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Dsh.Authorization - console assertions");
        Console.WriteLine();

        Run("Registry: registration projects an entry and disposal removes the flow", AuthorizationTests.Registration_ProjectsEntry_AndDisposalRemovesTheFlow);
        Run("Registry: duplicate registration fails loud", AuthorizationTests.DuplicateRegistration_FailsLoud);
        Run("Registry: a flow with no methods is rejected at registration", AuthorizationTests.FlowWithNoMethods_IsRejectedAtRegistration);
        Run("Registry: disposing the flow mid-attempt withdraws the attempt", AuthorizationTests.DisposingTheFlow_MidAttempt_WithdrawsTheAttempt);
        Run("Begin: a successful begin commits the record and settles authorized", AuthorizationTests.SuccessfulBegin_CommitsTheRecord_AndSettlesAuthorized);
        Run("Begin: notices and prompts travel between flow and surface", AuthorizationTests.Session_CarriesNoticesAndPrompts_BetweenFlowAndSurface);
        Run("Begin: method defaults to the first and honors the named one", AuthorizationTests.Method_DefaultsToTheFirst_AndHonorsTheNamedOne);
        Run("Begin: a key no flow claims fails loud", AuthorizationTests.UnknownKey_FailsLoud);
        Run("Begin: a method the flow does not offer fails loud", AuthorizationTests.UnknownMethod_FailsLoud);
        Run("Begin: a second attempt while one runs is refused, and the key is released after", AuthorizationTests.SecondAttempt_WhileInFlight_IsRefused_AndTheKeyIsReleasedAfter);
        Run("Begin: a caller withdrawn before begin gets cancelled without running the flow", AuthorizationTests.PreCancelledBegin_ReturnsCancelled_WithoutRunningTheFlow);
        Run("Begin: a cancelled attempt settles cancelled and commits nothing", AuthorizationTests.CancelledBegin_SettlesCancelled_AndCommitsNothing);
        Run("Begin: cancel() on an idle key is a no-op", AuthorizationTests.Cancel_OnAnIdleKey_IsANoOp);
        Run("Begin: a throwing flow fails its caller and settles failed on the event stream", AuthorizationTests.ThrowingFlow_FailsItsCaller_AndSettlesFailedOnTheEventStream);
        Run("Commit: a flow that resolves without committing fails loud", AuthorizationTests.FlowResolving_WithoutCommitting_FailsLoud);
        Run("Commit: a re-auth that left only an earlier record fails loud", AuthorizationTests.ReAuth_ThatLeftOnlyAnEarlierRecord_FailsLoud);
        Run("Commit: a flow that deletes its record instead of committing one fails loud", AuthorizationTests.FlowDeletingItsRecord_FailsLoud);
        Run("Prompt: a declined prompt settles cancelled, not failed", AuthorizationTests.DeclinedPrompt_SettlesCancelled_NotFailed);
        Run("Prompt: a decline is read through a flow that rewraps the rejection", AuthorizationTests.DeclineIsReadThroughAFlow_ThatRewrapsTheRejection);
        Run("Prompt: a prompt failure that is not a decline fails the attempt", AuthorizationTests.PromptFailure_ThatIsNotADecline_FailsTheAttempt);
        Run("Notice: a broken surface loses the notice, never the attempt", AuthorizationTests.BrokenNoticeSurface_LosesTheNotice_NeverTheAttempt);
        Run("Settled: a throwing listener is contained", AuthorizationTests.SettledFanOut_Contains_AThrowingListener);
        Run("Settled: fires after the slot is released", AuthorizationTests.SettleFires_AfterTheSlotIsReleased);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (_failed > 0)
        {
            foreach (var failure in Failures)
            {
                Console.WriteLine("  FAILED: " + failure);
            }
            return 1;
        }
        return 0;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (AssertionException ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }
}
