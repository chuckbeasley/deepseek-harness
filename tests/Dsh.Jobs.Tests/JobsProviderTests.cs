using Cordis.Core;
using Dsh.Jobs;

namespace Dsh.Jobs.Tests;

/// <summary>
/// The process-local job registry: id assignment, settlement, consuming reads, kill, teardown,
/// contained failures, access fencing, and listener notification.
/// </summary>
public static class JobsProviderTests
{
    public static void Start_AssignsIdsAndSettlesDone()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var done1 = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done2 = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var id1 = jobs.Start(new JobStartRequest("test", "first job", () => new JobHooks(_ => { }, done1.Task)));
        var id2 = jobs.Start(new JobStartRequest("test", "second job", () => new JobHooks(_ => { }, done2.Task)));

        Assert.Equal("test-1", id1.Value, "ids are <kind>-N with per-kind counters");
        Assert.Equal("test-2", id2.Value, "the second id of the same kind increments");
        Assert.Equal("running", JobStatuses.WireName(jobs.Get(id1).Status));

        done1.TrySetResult(new JobOutcome(JobStatus.Completed, Detail: "exit code 0"));
        var snapshot = jobs.WaitAsync(id1, timeoutMs: 5000).GetAwaiter().GetResult();

        Assert.Equal(JobStatus.Completed, snapshot.Status);
        Assert.Equal("exit code 0", snapshot.Detail);
        Assert.NotNull(snapshot.FinishedAt, "a terminal snapshot carries finishedAt");
        Assert.True(snapshot.FinishedAt >= snapshot.StartedAt, "finishedAt is no earlier than startedAt");
        Assert.True(snapshot.Reported, "a wait with a pending waiter marks the job reported");

        done2.TrySetResult(new JobOutcome(JobStatus.Completed));
        Assert.Equal(JobStatus.Completed, jobs.WaitAsync(id2, 5000).GetAwaiter().GetResult().Status);
    }

    public static void Read_StreamJobs_AreConsumingWithNoRedelivery()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var queue = new Queue<string>(new[] { "a", "b", "c" });
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = jobs.Start(new JobStartRequest("stream", "streaming", () => new JobHooks(
            _ => { },
            done.Task,
            ReadOutput: () => queue.Count > 0 ? queue.Dequeue() : string.Empty)));

        Assert.Equal("a", jobs.Read(id).Text, "the first read returns the first delta");
        Assert.Equal("b", jobs.Read(id).Text, "the second read returns only output since the previous read");
        Assert.Equal("c", jobs.Read(id).Text);
        Assert.Equal(string.Empty, jobs.Read(id).Text, "a drained stream read returns empty — no re-delivery");
        Assert.Equal(JobStatus.Running, jobs.Read(id).Snapshot.Status, "reads do not settle the job");

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
        Assert.Equal(JobStatus.Completed, jobs.WaitAsync(id, 5000).GetAwaiter().GetResult().Status);
    }

    public static void Read_FinalOutputJob_ReturnsOutputOnceSettled_Idempotently()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = jobs.Start(new JobStartRequest("final", "final-output", () => new JobHooks(_ => { }, done.Task)));

        Assert.Equal(string.Empty, jobs.Read(id).Text, "a final-output job reads empty while live");

        done.TrySetResult(new JobOutcome(JobStatus.Completed, Output: "the result"));
        Assert.Equal(JobStatus.Completed, jobs.WaitAsync(id, 5000).GetAwaiter().GetResult().Status);

        Assert.Equal("the result", jobs.Read(id).Text, "the terminal output is readable after settlement");
        Assert.Equal("the result", jobs.Read(id).Text, "the terminal output is idempotent — never consumed");
    }

    public static void Kill_MarksKilledAndResolves()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = jobs.Start(new JobStartRequest("test", "long-running", () => new JobHooks(
            reason => done.TrySetResult(new JobOutcome(JobStatus.Killed, Detail: $"cancelled: {reason}")),
            done.Task)));

        Assert.Equal(JobKillResult.Requested, jobs.Kill(id, reason: "user said stop"));
        var snapshot = jobs.WaitAsync(id, 5000).GetAwaiter().GetResult();

        Assert.Equal(JobStatus.Killed, snapshot.Status);
        Assert.Equal("cancelled: user said stop", snapshot.Detail, "the kill reason is forwarded to the producer");
        Assert.True(snapshot.Reported, "a kill claims the terminal report");
        Assert.Equal(JobKillResult.AlreadyFinished, jobs.Kill(id), "killing a settled job reports already-finished");
    }

    public static void Kill_OnTerminalJob_MarksReportedAndReturnsAlreadyFinished()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = jobs.Start(new JobStartRequest("test", "done", () => new JobHooks(_ => { }, done.Task)));

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
        jobs.WaitAsync(id, 5000).GetAwaiter().GetResult();

        Assert.Equal(JobKillResult.AlreadyFinished, jobs.Kill(id));
        Assert.True(jobs.Get(id).Reported);
    }

    public static void Teardown_KillsRunningJobsAndAwaits()
    {
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        jobs.Start(new JobStartRequest("test", "never-finishing", () => new JobHooks(
            _ => done.TrySetResult(new JobOutcome(JobStatus.Killed, Detail: "killed by teardown")),
            done.Task)));

        ctx.Dispose(); // service teardown cancels live work and awaits settlement

        Assert.True(done.Task.IsCompleted, "teardown cancel settled the producer's done promise");
        Assert.Equal(JobStatus.Killed, done.Task.Result.Status);
        Assert.Empty(jobs.List(), "teardown drops every record");
    }

    public static void FailedBodies_SettleWithErrorsContained()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);

        // A producer that resolves done with a failed outcome.
        var done1 = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id1 = jobs.Start(new JobStartRequest("test", "broke", () => new JobHooks(_ => { }, done1.Task)));
        done1.TrySetResult(new JobOutcome(JobStatus.Failed, Detail: "exit code 3"));
        var snapshot1 = jobs.WaitAsync(id1, 5000).GetAwaiter().GetResult();
        Assert.Equal(JobStatus.Failed, snapshot1.Status);
        Assert.Equal("exit code 3", snapshot1.Detail);

        // A producer that rejects done (contract violation) settles failed with the error contained.
        var done2 = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id2 = jobs.Start(new JobStartRequest("test", "rejects", () => new JobHooks(_ => { }, done2.Task)));
        done2.TrySetException(new InvalidOperationException("boom"));
        var snapshot2 = jobs.WaitAsync(id2, 5000).GetAwaiter().GetResult();
        Assert.Equal(JobStatus.Failed, snapshot2.Status);
        Assert.Contains("boom", snapshot2.Detail!, "a rejected done promise settles failed with the error message");
    }

    public static void Access_FencesOwnedJobsBySession()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doneUnowned = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var owned = jobs.Start(new JobStartRequest("test", "owned", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-a"));
        var unowned = jobs.Start(new JobStartRequest("test", "open", () => new JobHooks(_ => { }, doneUnowned.Task)));

        Assert.Throws<InvalidOperationException>(() => jobs.Get(owned, "session-b"), "a foreign caller cannot read another session's job");
        Assert.Throws<InvalidOperationException>(() => jobs.Read(owned, "session-b"));
        Assert.Throws<InvalidOperationException>(() => jobs.Kill(owned, "session-b"));
        Assert.Throws<InvalidOperationException>(() => jobs.WaitAsync(owned, 100, "session-b").GetAwaiter().GetResult());

        jobs.Get(owned, "session-a");
        jobs.Get(unowned, "session-b");

        var ownedList = jobs.List("session-a");
        Assert.True(ownedList.Any(job => job.Id == owned), "an owner lists its own job");
        Assert.True(ownedList.All(job => job.OwnerSession is null || job.OwnerSession == "session-a"),
            "an owner sees its own job and unowned jobs — never another session's labels");
        var strangerList = jobs.List("session-c");
        Assert.All(strangerList, job => Assert.Null(job.OwnerSession, "a stranger sees only unowned jobs"));

        Assert.Throws<InvalidOperationException>(() => jobs.Get(new JobId("nope-1")), "an unknown id fails loud");

        // Settle both jobs so teardown (which cancels and awaits) is not stalled.
        done.TrySetResult(new JobOutcome(JobStatus.Completed));
        doneUnowned.TrySetResult(new JobOutcome(JobStatus.Completed));
    }

    public static void DoneListener_FiresWithSnapshotAndOwner()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        JobSnapshot? received = null;
        string? receivedOwner = null;
        using var subscription = jobs.OnJobDone((snapshot, owner) =>
        {
            received = snapshot;
            receivedOwner = owner;
        });
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = jobs.Start(new JobStartRequest("test", "notify", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-a"));

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
        Assert.WaitUntil(() => received is not null, message: "the completion listener receives the settlement");

        Assert.Equal(id, received!.Id);
        Assert.Equal(JobStatus.Completed, received.Status);
        Assert.Equal("session-a", receivedOwner, "the listener receives the exact owner session id");
    }

    public static void ChangedListener_FiresOnRegistrationSettlementAndTeardown()
    {
        var owners = new List<string?>();
        var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        using var subscription = jobs.OnJobsChanged(owner => owners.Add(owner));

        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Start(new JobStartRequest("test", "changing", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-a"));
        Assert.True(owners.Contains("session-a"), "registration announces the visible-set change");

        done.TrySetResult(new JobOutcome(JobStatus.Completed));
        Assert.WaitUntil(() => owners.Count(owner => owner == "session-a") >= 2, message: "settlement announces the change");

        ctx.Dispose();
        Assert.True(owners.Count(owner => owner == "session-a") >= 3, "teardown emptying announces the change");
    }

    public static void Start_RefusesOverThePerOwnerLimit()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx, maxConcurrentJobsPerOwner: 1);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Start(new JobStartRequest("test", "one", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-a"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            jobs.Start(new JobStartRequest("test", "two", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-a")));
        Assert.Contains("background job limit reached", error.Message);

        // A different owner bucket is not affected (the ordinal counts successful starts per kind).
        var other = jobs.Start(new JobStartRequest("test", "other", () => new JobHooks(_ => { }, done.Task), OwnerSession: "session-b"));
        Assert.Equal("test-2", other.Value);

        // Settle the live jobs so teardown (which cancels and awaits) is not stalled.
        done.TrySetResult(new JobOutcome(JobStatus.Completed));
    }

    public static void Start_ValidatesInputLoud()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);
        var done = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Throws<ArgumentException>(() => jobs.Start(new JobStartRequest("", "label", () => new JobHooks(_ => { }, done.Task))), "an empty kind fails loud");
        Assert.Throws<ArgumentException>(() => jobs.Start(new JobStartRequest("test", "", () => new JobHooks(_ => { }, done.Task))), "an empty label fails loud");
        Assert.Throws<ArgumentException>(() => jobs.Start(new JobStartRequest("test", "label", () => new JobHooks(_ => { }, done.Task), OutputLimitBytes: 0)));

        var id = jobs.Start(new JobStartRequest("test", "valid", () => new JobHooks(_ => { }, done.Task)));
        Assert.Throws<ArgumentException>(() => jobs.WaitAsync(id, 0).GetAwaiter().GetResult(), "a non-positive wait timeout fails loud");
        Assert.Throws<InvalidOperationException>(() => jobs.WaitAsync(new JobId("nope-1"), 100).GetAwaiter().GetResult(), "an unknown job id fails loud");
        done.TrySetResult(new JobOutcome(JobStatus.Completed));
    }

    public static void Start_ThrowingStarter_LeavesNothingRegistered()
    {
        using var ctx = new Context();
        var jobs = new LocalJobsProvider(ctx);

        Assert.Throws<InvalidOperationException>(() => jobs.Start(new JobStartRequest(
            "test",
            "explodes",
            () => throw new InvalidOperationException("starter exploded"))));
        Assert.Empty(jobs.List(), "a throwing starter registers nothing");
    }
}
