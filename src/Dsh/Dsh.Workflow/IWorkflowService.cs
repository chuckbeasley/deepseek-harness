namespace Harness.Workflow;

/// <summary>
/// Service Definition of the workflow capability seam (ctx.workflow): register workflow definitions,
/// start runs, and observe run state. Service Providers execute the ordered steps on worker tasks;
/// observe-only lifecycle events never expose run control. Port of
/// <c>@deepseek-ai/dsh-workflow</c>.
///
/// Port deviations from the TS seam: the TS engine parses a model-written script body per start;
/// this port registers host-authored <see cref="WorkflowDefinition"/>s (ordered C# step delegates)
/// and starts a run by definition name, so there is no script parse or model-supplied meta. Runs
/// are recorded in the provider and observable through <see cref="List"/> / <see cref="Get"/> (the
/// TS exposes run state only through the returned handle and events). <c>agent()</c> fan-out events
/// are absent because the C# spine has no subagent seam in this wave.
/// </summary>
public interface IWorkflowService
{
    /// <summary>
    /// Register one workflow definition under its meta name. The registration is an effect: the
    /// returned disposer unregisters it. Misconfiguration fails loud (a duplicate name or a
    /// malformed meta block throws).
    /// </summary>
    /// <returns>the disposer that unregisters the definition.</returns>
    IDisposable Register(WorkflowDefinition definition);

    /// <summary>
    /// Start one run of a registered definition. Invalid requests throw before publication
    /// (<see cref="WorkflowError"/> for an unknown definition); a live run is holder-owned, its
    /// result never rejects, and cancellation is cooperative.
    /// </summary>
    /// <returns>the live run; its <see cref="WorkflowRun.Result"/> resolves when the steps settle.</returns>
    WorkflowRun Start(WorkflowRunStartRequest request);

    /// <summary>All recorded runs (live and settled), in start order, as fresh snapshots.</summary>
    IReadOnlyList<WorkflowRunSnapshot> List();

    /// <summary>One recorded run by id, or null when unknown.</summary>
    WorkflowRunSnapshot? Get(string runId);
}
