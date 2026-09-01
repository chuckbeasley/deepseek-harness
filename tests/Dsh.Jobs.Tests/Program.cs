namespace Dsh.Jobs.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("start assigns <kind>-N ids and settles done", JobsProviderTests.Start_AssignsIdsAndSettlesDone),
        ("stream reads are consuming with no re-delivery", JobsProviderTests.Read_StreamJobs_AreConsumingWithNoRedelivery),
        ("final-output jobs read idempotently after settlement", JobsProviderTests.Read_FinalOutputJob_ReturnsOutputOnceSettled_Idempotently),
        ("kill marks killed and resolves the done promise", JobsProviderTests.Kill_MarksKilledAndResolves),
        ("killing a terminal job reports already-finished", JobsProviderTests.Kill_OnTerminalJob_MarksReportedAndReturnsAlreadyFinished),
        ("teardown kills running jobs and awaits settlement", JobsProviderTests.Teardown_KillsRunningJobsAndAwaits),
        ("failed bodies settle with errors contained", JobsProviderTests.FailedBodies_SettleWithErrorsContained),
        ("owned jobs are fenced by session", JobsProviderTests.Access_FencesOwnedJobsBySession),
        ("completion listeners receive snapshot and owner", JobsProviderTests.DoneListener_FiresWithSnapshotAndOwner),
        ("visible-set observers fire on every commit", JobsProviderTests.ChangedListener_FiresOnRegistrationSettlementAndTeardown),
        ("start refuses over the per-owner limit", JobsProviderTests.Start_RefusesOverThePerOwnerLimit),
        ("start validates input loudly", JobsProviderTests.Start_ValidatesInputLoud),
        ("a throwing starter leaves nothing registered", JobsProviderTests.Start_ThrowingStarter_LeavesNothingRegistered),
        ("job_output reads incrementally through the registry", JobToolsTests.JobOutput_ReadsIncrementallyThroughTheTool),
        ("job_output wait returns terminal state", JobToolsTests.JobOutput_WaitTrue_ReturnsTerminalState),
        ("job_output wait times out with live state", JobToolsTests.JobOutput_WaitTrue_TimesOutWithLiveState),
        ("job_kill requests cancellation and reports already-finished", JobToolsTests.JobKill_RequestsCancellationAndReportsAlreadyFinished),
        ("job_list projects owned jobs", JobToolsTests.JobList_ListsOwnedJobs),
        ("job_output fails loud on an unknown job", JobToolsTests.JobOutput_UnknownJob_FailsLoud),
        ("job_output fails loud on an empty job id", JobToolsTests.JobOutput_EmptyJobId_FailsLoud),
        ("notice summary bounds to the context cap", JobNoticeTests.Bound_TruncatesPastTheSummaryCap),
        ("notice delivery lands an unreported completion in the next-step inbox", JobNoticeTests.Install_DeliversAnUnreportedCompletionIntoTheNextStepInbox),
        ("notice delivery skips a job the wait already reported", JobNoticeTests.Install_SkipsAJobTheWaitAlreadyReported),
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
