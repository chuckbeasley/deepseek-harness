namespace Harness.Workflow;

/// <summary>
/// Holder-owned live workflow run — the <see cref="IWorkflowService.Start"/> return. <see cref="Result"/>
/// never rejects; consumers may cancel and must await <see cref="DisposeAsync"/> to join settlement.
/// Port of the TS <c>WorkflowRun</c> handle: there is no worker thread to terminate in this port, so
/// cancellation is cooperative (steps observe the token) and <see cref="DisposeAsync"/> waits for the
/// result without a forced-termination grace.
/// </summary>
public sealed class WorkflowRun
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<WorkflowResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<WorkflowResult> _onSettled;
    private bool _settled;
    private bool _disposed;
    private string? _cancelReason;

    internal WorkflowRun(WorkflowRunId id, WorkflowMeta meta, Action<WorkflowResult> onSettled, CancellationToken externalToken)
    {
        Id = id;
        Meta = meta;
        _onSettled = onSettled ?? throw new ArgumentNullException(nameof(onSettled));
        if (externalToken.CanBeCanceled)
        {
            externalToken.Register(() => Cancel("workflow signal aborted"));
        }
    }

    /// <summary>The run's id.</summary>
    public WorkflowRunId Id { get; }

    /// <summary>The validated meta block available before the steps run.</summary>
    public WorkflowMeta Meta { get; }

    /// <summary>Resolves exactly once with the run's outcome; never rejects.</summary>
    public Task<WorkflowResult> Result => _result.Task;

    /// <summary>The run's cancellation token (cancelled by <see cref="Cancel"/>).</summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Cancel the run. Idempotent; the first reason wins. Steps observe the token and settle the
    /// run <c>cancelled</c> at their next cooperative check.
    /// </summary>
    public void Cancel(string? reason = null)
    {
        lock (_gate)
        {
            if (_settled) return;
            _cancelReason ??= reason ?? "workflow cancelled";
            _cts.Cancel();
        }
    }

    /// <summary>Cancel if needed and await settlement, resolving with the settled result. Idempotent; safe on every path.</summary>
    public Task<WorkflowResult> DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return _result.Task;
            _disposed = true;
        }
        Cancel("workflow disposed");
        return _result.Task;
    }

    /// <summary>The first cancellation reason, or null while uncancelled.</summary>
    internal string? CancelReason
    {
        get
        {
            lock (_gate) return _cancelReason;
        }
    }

    /// <summary>Record the first-wins terminal result and announce it through the provider hook.</summary>
    internal void Settle(WorkflowResult result)
    {
        lock (_gate)
        {
            if (_settled) return;
            _settled = true;
        }
        // Announce BEFORE resolving: observers awaiting Result must observe workflow/end first.
        _onSettled(result);
        _result.TrySetResult(result);
    }

    /// <summary>Start the run's worker task; the body must never throw.</summary>
    internal void StartRun(Func<Task<WorkflowResult>> run)
    {
        _ = RunAsync(run);
    }

    private async Task RunAsync(Func<Task<WorkflowResult>> run)
    {
        var result = await run().ConfigureAwait(false);
        Settle(result);
    }
}
