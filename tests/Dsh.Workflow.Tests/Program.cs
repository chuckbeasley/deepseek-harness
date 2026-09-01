namespace Dsh.Workflow.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("register + start a two-step workflow, steps run in order", WorkflowProviderTests.RegisterAndStart_TwoStepWorkflow_RunsStepsInOrder),
        ("steps run on worker tasks, not the caller's thread", WorkflowProviderTests.Steps_RunOnWorkerTasks),
        ("cancellation stops the run and skips later steps", WorkflowProviderTests.Cancellation_StopsTheRun),
        ("cancellation force-settles a step stuck in a wait", WorkflowProviderTests.Cancellation_MidStepDelay_ForceSettlesCancelled),
        ("the external start signal cancels the run", WorkflowProviderTests.ExternalStartSignal_CancelsTheRun),
        ("run state is observable through the provider", WorkflowProviderTests.RunState_IsObservableThroughTheProvider),
        ("workflow lifecycle events fire around the run", WorkflowProviderTests.LifecycleEvents_FireAroundTheRun),
        ("a step failure settles the run with the error", WorkflowProviderTests.StepFailure_SettlesErrorWithMessage),
        ("an unknown definition fails loud", WorkflowProviderTests.UnknownDefinition_FailsLoud),
        ("register validates meta and rejects duplicates", WorkflowProviderTests.Register_ValidatesMetaAndRejectsDuplicates),
        ("dispose joins settlement and is idempotent", WorkflowProviderTests.Dispose_JoinsSettlementAndIsIdempotent),
        ("dispose cancels an unsettled run", WorkflowProviderTests.Dispose_CancelsAnUnsettledRun),
        ("the workflow tool runs a registered definition and records it", WorkflowToolsTests.WorkflowTool_ExecutesRegisteredDefinition_AndRecordsTheRun),
        ("the workflow tool maps a failing step to an error result", WorkflowToolsTests.WorkflowTool_ErrorStep_IsAnErrorResult),
        ("the workflow tool fails loud on an unknown definition", WorkflowToolsTests.WorkflowTool_UnknownDefinition_FailsLoud),
        ("the workflow tool fails loud on an empty definition name", WorkflowToolsTests.WorkflowTool_EmptyDefinitionName_FailsLoud),
        ("stop-reason errors map every non-completed reason", WorkflowToolsTests.StopReasonError_MapsEveryNonCompletedReason),
        ("the result renderer caps long values", WorkflowToolsTests.RenderResult_CapsLongValues),
        ("ralph: the worker prompt matches the recorded wording", RalphToolTests.BuildPrompt_MatchesTheRecordedWorkerWording),
        ("ralph: the recorded round reports validate", RalphToolTests.ValidateReport_AcceptsTheRecordedReports),
        ("ralph: malformed reports fail loud", RalphToolTests.ValidateReport_RejectsMalformedReports),
        ("ralph: structured_output renders the recorded line", RalphToolTests.StructuredOutputTool_RendersTheRecordedText),
        ("ralph: the terminal envelope matches the recording", RalphToolTests.RenderResult_MatchesTheRecordedEnvelope),
        ("workflow: the agent label truncates the prompt", WorkflowToolTests.DefaultLabel_TruncatesThePromptFirstLineAt47),
        ("workflow: the pretty JSON matches the recording", WorkflowToolTests.PrettyJson_MatchesTheRecordedSpelling),
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
