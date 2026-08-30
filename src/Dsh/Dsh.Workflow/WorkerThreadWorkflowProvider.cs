using Cordis.Core;
using Dsh.Llm;

namespace Dsh.Workflow;

/// <summary>The provider's mutable per-run record (never handed out — see <see cref="Snapshot"/>).</summary>
internal sealed class RunRecord
{
    public required WorkflowRun Run { get; init; }

    public required WorkflowDefinition Definition { get; init; }

    public object? Args { get; init; }

    public string? ParentSession { get; init; }

    public long StartedAt { get; init; }

    public bool Settled { get; set; }

    public WorkflowStopReason? StopReason { get; set; }

    public string? Error { get; set; }

    public int StepsStarted { get; set; }

    public long? FinishedAt { get; set; }

    public WorkflowRunSnapshot Snapshot() => new(
        Run.Id,
        Run.Meta,
        Settled,
        StopReason,
        Error,
        StepsStarted,
        StartedAt,
        FinishedAt);
}

/// <summary>
/// Worker-task workflow engine (ctx.workflow). Each run executes its registered definition's
/// ordered steps, scheduling every step on a thread-pool worker task so a slow or blocking step
/// never occupies the caller's thread; a step throw or cancellation settles the run. The
/// <c>workflow/*</c> lifecycle events fire around the run per the seam contract. Port of
/// <c>@deepseek-ai/dsh-workflow-worker-thread</c>: thread-pool tasks replace worker threads (there
/// is no escapable script realm to isolate), cancellation is cooperative rather than forced
/// termination, and the service key is <c>workflow</c> (the TS registers <c>workflowEngine</c>).
/// </summary>
public sealed class WorkerThreadWorkflowProvider : Service, IWorkflowService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkflowDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<WorkflowRunId, RunRecord> _runs = new();

    /// <summary>Create and register the engine under the <c>workflow</c> key.</summary>
    public WorkerThreadWorkflowProvider(Context ctx)
        : base(ctx, "workflow")
    {
    }

    /// <inheritdoc />
    public IDisposable Register(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        return Ctx.Effect(() =>
        {
            lock (_gate)
            {
                if (_definitions.ContainsKey(definition.Meta.Name))
                {
                    throw new InvalidOperationException($"workflow definition \"{definition.Meta.Name}\" is already registered");
                }
                _definitions[definition.Meta.Name] = definition;
            }
            return new ActionDisposer(() =>
            {
                lock (_gate) _definitions.Remove(definition.Meta.Name);
            });
        }, "workflow.register()");
    }

    /// <inheritdoc />
    public WorkflowRun Start(WorkflowRunStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkflowDefinition definition;
        lock (_gate)
        {
            if (!_definitions.TryGetValue(request.DefinitionName, out definition!))
            {
                throw new WorkflowError($"workflow definition \"{request.DefinitionName}\" is not registered", WorkflowErrorCode.InvalidArgument);
            }
        }
        var id = new WorkflowRunId(Guid.NewGuid().ToString("D"));
        var run = new WorkflowRun(id, definition.Meta, result => OnRunSettled(id, result), request.CancellationToken);
        var record = new RunRecord
        {
            Run = run,
            Definition = definition,
            Args = request.Args,
            ParentSession = request.ParentSession,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (_gate) _runs[id] = record;
        EmitContained("workflow/start", new WorkflowRunInfo(id, definition.Meta));
        run.StartRun(() => ExecuteAsync(record, run));
        return run;
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkflowRunSnapshot> List()
    {
        lock (_gate)
        {
            return _runs.Values.Select(record => record.Snapshot()).ToArray();
        }
    }

    /// <inheritdoc />
    public WorkflowRunSnapshot? Get(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        lock (_gate)
        {
            return _runs.TryGetValue(new WorkflowRunId(runId), out var record) ? record.Snapshot() : null;
        }
    }

    /// <summary>
    /// Run the definition's steps in order, each on a worker task. The first cancellation or step
    /// failure settles the run; the result never throws.
    /// </summary>
    private async Task<WorkflowResult> ExecuteAsync(RunRecord record, WorkflowRun run)
    {
        var stepsStarted = 0;
        object? value = null;
        try
        {
            foreach (var step in record.Definition.Steps)
            {
                run.CancellationToken.ThrowIfCancellationRequested();
                stepsStarted += 1;
                record.StepsStarted = stepsStarted;
                var context = new WorkflowStepContext(
                    record.Args,
                    title => EmitPhase(run.Id, title),
                    message => EmitLog(run.Id, message),
                    run.CancellationToken);
                value = await Task.Run(() => step(context, run.CancellationToken), run.CancellationToken).ConfigureAwait(false);
            }
            return new WorkflowResult(value, WorkflowStopReason.Completed, StepsStarted: stepsStarted);
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested)
        {
            var reason = run.CancelReason ?? "workflow cancelled";
            return new WorkflowResult(null, WorkflowStopReason.Cancelled, Error: $"workflow run cancelled: {reason}", StepsStarted: stepsStarted);
        }
        catch (Exception error)
        {
            return new WorkflowResult(null, WorkflowStopReason.Error, Error: error.Message, StepsStarted: stepsStarted);
        }
    }

    /// <summary>Record the settled snapshot and emit <c>workflow/end</c> with the outcome data only.</summary>
    private void OnRunSettled(WorkflowRunId id, WorkflowResult result)
    {
        RunRecord? record;
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out record)) return;
            record.Settled = true;
            record.StopReason = result.StopReason;
            record.Error = result.Error;
            record.StepsStarted = result.StepsStarted;
            record.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        EmitContained("workflow/end", new WorkflowRunInfo(id, record.Definition.Meta),
            new WorkflowResultInfo(result.StopReason, result.Error, result.StepsStarted));
    }

    private void EmitPhase(WorkflowRunId id, string title)
    {
        var info = InfoOf(id);
        if (info is not null) EmitContained("workflow/phase", info, title);
    }

    private void EmitLog(WorkflowRunId id, string message)
    {
        var info = InfoOf(id);
        if (info is not null) EmitContained("workflow/log", info, message);
    }

    private WorkflowRunInfo? InfoOf(WorkflowRunId id)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(id, out var record) ? new WorkflowRunInfo(id, record.Definition.Meta) : null;
        }
    }

    /// <summary>Emit one lifecycle event, containing and logging each listener failure.</summary>
    private void EmitContained(string name, params object?[] args)
    {
        try
        {
            Ctx.Emit(name, args);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"workflow: {name} listener threw: {error.Message}");
        }
    }

    /// <summary>Validate a definition's meta contract and step count, naming every violation.</summary>
    /// <exception cref="WorkflowError">with code <see cref="WorkflowErrorCode.MetaInvalid"/> on a malformed definition.</exception>
    private static void ValidateDefinition(WorkflowDefinition definition)
    {
        var violations = new List<string>();
        var meta = definition.Meta;
        if (string.IsNullOrEmpty(meta.Name)) violations.Add("meta.name must be a non-empty string");
        if (string.IsNullOrEmpty(meta.Description)) violations.Add("meta.description must be a non-empty string");
        if (meta.Phases is not null)
        {
            for (var index = 0; index < meta.Phases.Count; index++)
            {
                if (string.IsNullOrEmpty(meta.Phases[index].Title))
                {
                    violations.Add($"meta.phases[{index}].title must be a non-empty string");
                }
            }
        }
        if (definition.Steps is null || definition.Steps.Count == 0)
        {
            violations.Add("definition must carry at least one step");
        }
        if (violations.Count > 0)
        {
            throw new WorkflowError($"invalid workflow definition: {string.Join("; ", violations)}", WorkflowErrorCode.MetaInvalid);
        }
    }
}
