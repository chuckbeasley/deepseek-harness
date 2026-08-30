using Cordis.Core;
using Dsh.Workflow;

namespace Dsh.Workflow.Tests;

/// <summary>
/// The worker-task workflow engine: register + start, ordered execution on worker tasks,
/// cancellation, observable run state, lifecycle events, and loud misconfiguration.
/// </summary>
public static class WorkflowProviderTests
{
    private static WorkflowDefinition TwoStep(string name, IReadOnlyList<WorkflowStep> steps)
        => new(new WorkflowMeta(name, "two ordered steps"), steps);

    public static void RegisterAndStart_TwoStepWorkflow_RunsStepsInOrder()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        var order = new List<string>();
        using var registration = workflow.Register(TwoStep("two-step", new WorkflowStep[]
        {
            (context, ct) =>
            {
                order.Add("step-1");
                return Task.FromResult<object?>("one");
            },
            (context, ct) =>
            {
                order.Add("step-2");
                return Task.FromResult<object?>((string)context.Args! + "-two");
            },
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("two-step", Args: "one"));
        var result = run.Result.GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);
        Assert.Equal(2, result.StepsStarted);
        Assert.Equal("one-two", result.Value, "the final step's value is the run result");
        Assert.Equal(new[] { "step-1", "step-2" }, order.ToArray(), "steps run in registration order");
        Assert.Equal("two-step", run.Meta.Name);
    }

    public static void Steps_RunOnWorkerTasks()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        var mainThreadId = Environment.CurrentManagedThreadId;
        var stepThreads = new List<int>();
        using var registration = workflow.Register(TwoStep("worker-ran", new WorkflowStep[]
        {
            (context, ct) =>
            {
                stepThreads.Add(Environment.CurrentManagedThreadId);
                return Task.FromResult<object?>(1);
            },
            (context, ct) =>
            {
                stepThreads.Add(Environment.CurrentManagedThreadId);
                return Task.FromResult<object?>(2);
            },
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("worker-ran"));
        var result = run.Result.GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);
        Assert.Equal(2, stepThreads.Count);
        Assert.All(stepThreads, threadId => Assert.True(threadId != mainThreadId, "each step ran on a worker task, not the caller's thread"));
    }

    public static void Cancellation_StopsTheRun()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        var step2Ran = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = workflow.Register(TwoStep("cancel-me", new WorkflowStep[]
        {
            async (context, ct) =>
            {
                await release.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return null;
            },
            (context, ct) =>
            {
                step2Ran = true;
                return Task.FromResult<object?>("never");
            },
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("cancel-me"));
        run.Cancel("test cancellation");
        release.TrySetResult();
        var result = run.Result.GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Cancelled, result.StopReason);
        Assert.Contains("test cancellation", result.Error!, "the first cancellation reason is forwarded to the result");
        Assert.Equal(1, result.StepsStarted, "only the running step started");
        Assert.False(step2Ran, "a cancelled run never starts later steps");
    }

    public static void Cancellation_MidStepDelay_ForceSettlesCancelled()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        using var registration = workflow.Register(TwoStep("slow-cancel", new WorkflowStep[]
        {
            async (context, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return null;
            },
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("slow-cancel"));
        run.Cancel("stop the wait");
        var result = run.Result.GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Cancelled, result.StopReason);
        Assert.Contains("stop the wait", result.Error!);
    }

    public static void ExternalStartSignal_CancelsTheRun()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = workflow.Register(TwoStep("signal-cancel", new WorkflowStep[]
        {
            async (context, ct) =>
            {
                await gate.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return null;
            },
        }));
        using var cts = new CancellationTokenSource();

        var run = workflow.Start(new WorkflowRunStartRequest("signal-cancel", CancellationToken: cts.Token));
        cts.Cancel();
        gate.TrySetResult();
        var result = run.Result.GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Cancelled, result.StopReason);
    }

    public static void RunState_IsObservableThroughTheProvider()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = workflow.Register(TwoStep("observable", new WorkflowStep[]
        {
            async (context, ct) =>
            {
                await gate.Task.ConfigureAwait(false);
                return "mid";
            },
            (context, ct) => Task.FromResult<object?>("done"),
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("observable"));
        var live = Assert.Single(workflow.List());
        Assert.Equal(run.Id, live.Id);
        Assert.False(live.Settled, "a live run is not settled");
        Assert.Null(live.StopReason);

        gate.TrySetResult();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);

        var settled = workflow.Get(run.Id.Value);
        Assert.NotNull(settled);
        Assert.True(settled!.Settled);
        Assert.Equal(WorkflowStopReason.Completed, settled.StopReason);
        Assert.Equal(2, settled.StepsStarted);
        Assert.NotNull(settled.FinishedAt);
        Assert.True(settled.FinishedAt >= settled.StartedAt, "finishedAt is no earlier than startedAt");
        Assert.Null(workflow.Get("no-such-run"), "an unknown run id reads null");
    }

    public static void LifecycleEvents_FireAroundTheRun()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        var events = new List<string>();
        using var startSub = ctx.On<WorkflowRunInfo>("workflow/start", info => events.Add($"start:{info.Meta.Name}"));
        using var phaseSub = ctx.On("workflow/phase", (Delegate)(Action<WorkflowRunInfo, string>)((_, title) => events.Add($"phase:{title}")));
        using var logSub = ctx.On("workflow/log", (Delegate)(Action<WorkflowRunInfo, string>)((_, message) => events.Add($"log:{message}")));
        using var endSub = ctx.On("workflow/end", (Delegate)(Action<WorkflowRunInfo, WorkflowResultInfo>)((_, info) => events.Add($"end:{WorkflowStopReasons.WireName(info.StopReason)}")));
        using var registration = workflow.Register(TwoStep("eventful", new WorkflowStep[]
        {
            (context, ct) =>
            {
                context.Phase("phase-one");
                context.Log("working");
                return Task.FromResult<object?>(null);
            },
            (context, ct) => Task.FromResult<object?>(null),
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("eventful"));
        run.Result.GetAwaiter().GetResult();

        Assert.Equal(0, events.IndexOf("start:eventful"), "workflow/start opens the run");
        Assert.True(events.Contains("phase:phase-one"), "a phase() call narrates the phase");
        Assert.True(events.Contains("log:working"), "a log() call narrates the line");
        Assert.Equal(events.Count - 1, events.IndexOf("end:completed"), "workflow/end closes the run");
        Assert.True(events.IndexOf("phase:phase-one") < events.IndexOf("end:completed"), "narration precedes the end event");
    }

    public static void StepFailure_SettlesErrorWithMessage()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        using var registration = workflow.Register(TwoStep("fails", new WorkflowStep[]
        {
            (context, ct) => throw new InvalidOperationException("step exploded"),
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("fails"));
        var result = run.Result.GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Error, result.StopReason);
        Assert.Contains("step exploded", result.Error!);
        Assert.Equal(1, result.StepsStarted);
    }

    public static void UnknownDefinition_FailsLoud()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);

        var error = Assert.Throws<WorkflowError>(() => workflow.Start(new WorkflowRunStartRequest("missing")));
        Assert.Equal(WorkflowErrorCode.InvalidArgument, error.Code);
        Assert.Contains("missing", error.Message);
    }

    public static void Register_ValidatesMetaAndRejectsDuplicates()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);

        var metaError = Assert.Throws<WorkflowError>(() => workflow.Register(
            new WorkflowDefinition(new WorkflowMeta("", "no name"), new[] { (WorkflowStep)((_, _) => Task.FromResult<object?>(null)) })));
        Assert.Equal(WorkflowErrorCode.MetaInvalid, metaError.Code);
        Assert.Contains("meta.name", metaError.Message);

        Assert.Throws<WorkflowError>(() => workflow.Register(
            new WorkflowDefinition(new WorkflowMeta("steps", "no steps"), Array.Empty<WorkflowStep>())));
        Assert.Throws<WorkflowError>(() => workflow.Register(
            new WorkflowDefinition(new WorkflowMeta("phases", "bad phase", Phases: new[] { new WorkflowPhase("") }), new[] { (WorkflowStep)((_, _) => Task.FromResult<object?>(null)) })));

        var disposer = workflow.Register(TwoStep("dup", new[] { (WorkflowStep)((_, _) => Task.FromResult<object?>(null)) }));
        Assert.Throws<InvalidOperationException>(() => workflow.Register(TwoStep("dup", new[] { (WorkflowStep)((_, _) => Task.FromResult<object?>(null)) })), "a duplicate name fails loud");

        disposer.Dispose();
        var after = workflow.Register(TwoStep("dup", new[] { (WorkflowStep)((_, _) => Task.FromResult<object?>(null)) }));
        after.Dispose();
    }

    public static void Dispose_JoinsSettlementAndIsIdempotent()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        using var registration = workflow.Register(TwoStep("disposable", new[] { (WorkflowStep)((_, _) => Task.FromResult<object?>("value")) }));

        var run = workflow.Start(new WorkflowRunStartRequest("disposable"));
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);

        // Dispose after settlement is a no-op that still returns the resolved result; Cancel is moot.
        run.DisposeAsync().GetAwaiter().GetResult();
        run.Cancel("too late");
        Assert.Equal(WorkflowStopReason.Completed, run.Result.GetAwaiter().GetResult().StopReason);
    }

    public static void Dispose_CancelsAnUnsettledRun()
    {
        using var ctx = new Context();
        var workflow = new WorkerThreadWorkflowProvider(ctx);
        using var registration = workflow.Register(TwoStep("dispose-cancel", new WorkflowStep[]
        {
            async (context, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return null;
            },
        }));

        var run = workflow.Start(new WorkflowRunStartRequest("dispose-cancel"));
        var result = run.DisposeAsync().GetAwaiter().GetResult();

        Assert.Equal(WorkflowStopReason.Cancelled, result.StopReason);
        Assert.Contains("workflow disposed", result.Error!);
    }
}
