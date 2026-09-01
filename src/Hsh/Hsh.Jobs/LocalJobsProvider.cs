using Harness.Cordis.Core;
using Harness.Llm;

namespace Harness.Jobs;

/// <summary>
/// The registry's mutable per-job record (never handed out — see <see cref="Snapshot"/>).
/// </summary>
internal sealed class TrackedJob
{
    public required JobId Id { get; init; }

    public required string Kind { get; init; }

    public required string Label { get; init; }

    public int? OutputLimitBytes { get; init; }

    /// <summary>Exact lifecycle owner session; authorization is derived from it.</summary>
    public string? OwnerSession { get; init; }

    public required Action<string?> Cancel { get; init; }

    public Func<string>? ReadOutput { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Running;

    public string? Detail { get; set; }

    public string? Output { get; set; }

    public long StartedAt { get; init; }

    public long? FinishedAt { get; set; }

    public bool Reported { get; set; }

    /// <summary>Resolves once the terminal snapshot is recorded and listeners notified.</summary>
    public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Live waits; settlement with a waiter marks the job reported.</summary>
    public int Waiters { get; set; }

    /// <summary>Removable resolvers for live waits; timeout/abort unregister before the job settles.</summary>
    public List<Action> WaitResolvers { get; } = new();
}

/// <summary>
/// Process-local provider for the background-job capability seam (ctx.jobs). It keeps every record
/// in memory and hands out fresh snapshots, never live state. Service disposal cancels live work and
/// awaits compliant producers; a throwing teardown cancel force-fails only the record and reports a
/// possible orphan. Port of <c>@deepseek-ai/hsh-jobs-local</c> (no scope-layered controllers or
/// agent-owned cleanup in this port — the tools themselves serve as the controller, and teardown is
/// service disposal).
/// </summary>
public sealed class LocalJobsProvider : Service, IJobsService
{
    /// <summary>Default maximum number of active jobs in one exact-owner bucket.</summary>
    public const int DefaultMaxConcurrentJobsPerOwner = 10;

    /// <summary>How long teardown waits for a cancelled producer to settle before force-failing its record.</summary>
    public const int TeardownGraceMs = 5000;

    private readonly object _gate = new();
    private readonly int _maxConcurrentJobsPerOwner;
    private readonly Dictionary<JobId, TrackedJob> _store = new();
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly List<Action<JobSnapshot, string?>> _doneListeners = new();
    private readonly List<Action<string?>> _changedListeners = new();
    private bool _listenersClosed;

    /// <summary>Create and register the registry under the <c>jobs</c> key.</summary>
    public LocalJobsProvider(Context ctx, int? maxConcurrentJobsPerOwner = null)
        : base(ctx, "jobs")
    {
        _maxConcurrentJobsPerOwner = maxConcurrentJobsPerOwner ?? DefaultMaxConcurrentJobsPerOwner;
    }

    /// <inheritdoc />
    public JobId Start(JobStartRequest spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Kind.Length == 0) throw new ArgumentException("invalid job kind: expected a non-empty string", nameof(spec));
        if (spec.Label.Length == 0) throw new ArgumentException("invalid job label: expected a non-empty string", nameof(spec));
        if (spec.OutputLimitBytes is { } limit && (limit <= 0))
        {
            throw new ArgumentException($"invalid outputLimitBytes: expected a positive safe integer, got {limit}", nameof(spec));
        }
        if (ActiveTaskCount(spec.OwnerSession) >= _maxConcurrentJobsPerOwner)
        {
            throw new InvalidOperationException(
                $"background job limit reached for this owner (limit: {_maxConcurrentJobsPerOwner}); use job_kill to stop an unneeded job, wait for it to finish, then retry");
        }

        // Preflight cannot fail from here: the starter runs once and registration cannot fail after.
        var hooks = spec.Run();
        var job = new TrackedJob
        {
            Id = new JobId($"{spec.Kind}-{NextOrdinal(spec.Kind)}"),
            Kind = spec.Kind,
            Label = spec.Label,
            OutputLimitBytes = spec.OutputLimitBytes,
            OwnerSession = spec.OwnerSession,
            Cancel = hooks.Cancel,
            ReadOutput = hooks.ReadOutput,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (_gate)
        {
            _store[job.Id] = job;
        }
        _ = ObserveDoneAsync(job, hooks.Done);
        NotifyChanged(job.OwnerSession);
        return job.Id;
    }

    /// <inheritdoc />
    public IReadOnlyList<JobSnapshot> List(string? callerSession = null)
    {
        lock (_gate)
        {
            return _store.Values
                .Where(job => job.OwnerSession is null || job.OwnerSession == callerSession)
                .Select(Snapshot)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public JobSnapshot Get(JobId id, string? callerSession = null)
    {
        lock (_gate)
        {
            var job = Expect(id);
            AssertAccess(job, callerSession);
            return Snapshot(job);
        }
    }

    /// <inheritdoc />
    public JobRead Read(JobId id, string? callerSession = null)
    {
        lock (_gate)
        {
            var job = Expect(id);
            AssertAccess(job, callerSession);
            var text = job.ReadOutput is not null
                ? job.ReadOutput()
                : JobStatuses.IsTerminal(job.Status) ? job.Output ?? string.Empty : string.Empty;
            if (JobStatuses.IsTerminal(job.Status)) job.Reported = true;
            return new JobRead(text, Snapshot(job));
        }
    }

    /// <inheritdoc />
    public JobKillResult Kill(JobId id, string? callerSession = null, string? reason = null)
    {
        lock (_gate)
        {
            var job = Expect(id);
            AssertAccess(job, callerSession);
            if (JobStatuses.IsTerminal(job.Status))
            {
                job.Reported = true;
                return JobKillResult.AlreadyFinished;
            }
            // Cancel first so a throw leaves both lifecycle and notice state unchanged.
            job.Cancel(reason);
            job.Status = JobStatus.Stopping;
            job.Reported = true;
        }
        NotifyChanged(OwnerSessionOf(id));
        return JobKillResult.Requested;
    }

    /// <inheritdoc />
    public async Task<JobSnapshot> WaitAsync(JobId id, int timeoutMs, string? callerSession = null, CancellationToken cancellationToken = default)
    {
        TrackedJob? job;
        Task? waitTask = null;
        Action? resolver = null;
        lock (_gate)
        {
            job = Expect(id);
            AssertAccess(job, callerSession);
            if (timeoutMs <= 0)
            {
                throw new ArgumentException($"invalid wait timeout: expected a positive number of milliseconds, got {timeoutMs}", nameof(timeoutMs));
            }
            if (!JobStatuses.IsTerminal(job.Status))
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.Waiters += 1;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                resolver = () => tcs.TrySetResult();
                job.WaitResolvers.Add(resolver);
                waitTask = tcs.Task;
            }
        }
        if (waitTask is not null)
        {
            try
            {
                var delay = Task.Delay(timeoutMs, cancellationToken);
                var completed = await Task.WhenAny(waitTask, delay).ConfigureAwait(false);
                if (!ReferenceEquals(completed, waitTask) && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("wait aborted", cancellationToken);
                }
            }
            finally
            {
                lock (_gate)
                {
                    job!.Waiters -= 1;
                    job.WaitResolvers.Remove(resolver!);
                }
            }
        }
        lock (_gate)
        {
            if (JobStatuses.IsTerminal(job!.Status)) job.Reported = true;
            return Snapshot(job);
        }
    }

    /// <inheritdoc />
    public IDisposable OnJobDone(Action<JobSnapshot, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate) _doneListeners.Add(listener);
        // The registration is an effect so a disposing fiber still validates activity; the effect
        // cleanup is a no-op because the service owns listener lifetime — StopAsync announces the
        // teardown emptying through the still-registered listeners before clearing the lists. The
        // returned disposer unregisters immediately.
        Ctx.Effect(() => new ActionDisposer(() => { }), "jobs.onJobDone()");
        return new ActionDisposer(() =>
        {
            lock (_gate) _doneListeners.Remove(listener);
        });
    }

    /// <inheritdoc />
    public IDisposable OnJobsChanged(Action<string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate) _changedListeners.Add(listener);
        Ctx.Effect(() => new ActionDisposer(() => { }), "jobs.onJobsChanged()");
        return new ActionDisposer(() =>
        {
            lock (_gate) _changedListeners.Remove(listener);
        });
    }

    /// <summary>
    /// Teardown: close listeners, cancel live jobs, await settlement bounded by a grace, and drop
    /// every record. A throwing cancel force-fails the record immediately; a cancel that returns
    /// without settling (indistinguishable from a slow stop) force-fails its record when the grace
    /// expires, so teardown can never hang the process on an uncooperative producer.
    /// </summary>
    public override async ValueTask StopAsync()
    {
        TrackedJob[] all;
        lock (_gate)
        {
            _listenersClosed = true;
            all = _store.Values.ToArray();
        }
        CancelForTeardown(all, "jobs service disposed");
        var allSettled = Task.WhenAll(all.Select(job => job.Settled.Task));
        var grace = Task.Delay(TeardownGraceMs);
        if (await Task.WhenAny(allSettled, grace).ConfigureAwait(false) == grace)
        {
            foreach (var job in all.Where(job => !job.Settled.Task.IsCompleted))
            {
                Ctx.Logger.Warn($"jobs: cancel of {job.Id} did not settle during teardown; job record forced failed and work may be orphaned");
                Settle(job, new JobOutcome(JobStatus.Failed, Detail: "cancel did not settle during teardown; work may be orphaned"));
            }
        }
        await allSettled.ConfigureAwait(false);
        string?[] emptied;
        lock (_gate)
        {
            emptied = all.Select(job => job.OwnerSession).Distinct().ToArray()!;
            foreach (var job in all) _store.Remove(job.Id);
        }
        foreach (var owner in emptied) NotifyChanged(owner);
        lock (_gate)
        {
            _doneListeners.Clear();
            _changedListeners.Clear();
        }
    }

    /// <summary>Count authoritative active records for one exact owner session or the shared unowned bucket.</summary>
    private int ActiveTaskCount(string? ownerSession)
    {
        lock (_gate)
        {
            return _store.Values.Count(job =>
                job.OwnerSession == ownerSession
                && (job.Status == JobStatus.Running || job.Status == JobStatus.Stopping));
        }
    }

    private int NextOrdinal(string kind)
    {
        lock (_gate)
        {
            var count = (_counters.TryGetValue(kind, out var existing) ? existing : 0) + 1;
            _counters[kind] = count;
            return count;
        }
    }

    /// <summary>Look up a job or fail loud.</summary>
    private TrackedJob Expect(JobId id)
    {
        if (!_store.TryGetValue(id, out var job))
        {
            throw new InvalidOperationException($"unknown job {id}");
        }
        return job;
    }

    /// <summary>
    /// The isolation fence: a job with an owner is reachable only by callers whose session id
    /// matches; an unowned job is open, and a no-session caller can never match an owned one.
    /// </summary>
    private static void AssertAccess(TrackedJob job, string? callerSession)
    {
        if (job.OwnerSession is not null && job.OwnerSession != callerSession)
        {
            throw new InvalidOperationException($"job {job.Id} belongs to another session");
        }
    }

    /// <summary>Project a fresh read-only snapshot from the mutable record.</summary>
    private static JobSnapshot Snapshot(TrackedJob job) => new(
        job.Id,
        job.Kind,
        job.Label,
        job.OutputLimitBytes,
        job.OwnerSession,
        job.Status,
        job.Detail,
        job.StartedAt,
        job.FinishedAt,
        job.Reported);

    private string? OwnerSessionOf(JobId id)
    {
        lock (_gate)
        {
            return _store.TryGetValue(id, out var job) ? job.OwnerSession : null;
        }
    }

    /// <summary>
    /// Announce that one owner's visible set changed. Each listener is contained so an observer
    /// cannot break a lifecycle commit that already happened.
    /// </summary>
    private void NotifyChanged(string? ownerSession)
    {
        Action<string?>[] listeners;
        lock (_gate) listeners = _changedListeners.ToArray();
        foreach (var listener in listeners)
        {
            try
            {
                listener(ownerSession);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn($"jobs: onJobsChanged listener threw: {error.Message}");
            }
        }
    }

    /// <summary>
    /// Observe one producer's done promise and settle the record. A rejection is a producer
    /// contract violation: contained so cleanup and waiters cannot hang.
    /// </summary>
    private async Task ObserveDoneAsync(TrackedJob job, Task<JobOutcome> done)
    {
        JobOutcome outcome;
        try
        {
            outcome = await done.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"jobs: job {job.Id} producer done promise rejected (producer contract violation): {error.Message}");
            outcome = new JobOutcome(JobStatus.Failed, Detail: error.Message);
        }
        Settle(job, outcome);
    }

    /// <summary>
    /// Record the first terminal outcome, release waiters, then announce completion. First-wins
    /// preserves a teardown force-failure against late producer settlement. Pending waits mark the
    /// job reported before listeners run. Completion is announced last because a reporter may open
    /// a model turn synchronously.
    /// </summary>
    private void Settle(TrackedJob job, JobOutcome outcome)
    {
        Action<JobSnapshot, string?>[] listeners;
        Action[] waitResolvers;
        JobSnapshot snapshot;
        string? ownerSession;
        lock (_gate)
        {
            if (JobStatuses.IsTerminal(job.Status)) return;
            job.Status = outcome.Status;
            job.Detail = outcome.Detail;
            job.Output = outcome.Output;
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (job.Waiters > 0) job.Reported = true;
            snapshot = Snapshot(job);
            waitResolvers = job.WaitResolvers.ToArray();
            job.WaitResolvers.Clear();
            job.Settled.TrySetResult();
            ownerSession = job.OwnerSession;
            listeners = _doneListeners.ToArray();
        }
        foreach (var resolveWait in waitResolvers) resolveWait();
        NotifyChanged(ownerSession);
        if (_listenersClosed) return;
        foreach (var listener in listeners)
        {
            try
            {
                listener(snapshot, ownerSession);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn($"jobs: onJobDone listener threw for {job.Id}: {error.Message}");
            }
        }
    }

    /// <summary>
    /// Cancel jobs during teardown with per-job containment. A throwing cancel force-fails the
    /// record and reports a possible orphan.
    /// </summary>
    private void CancelForTeardown(IReadOnlyList<TrackedJob> jobs, string reason)
    {
        foreach (var job in jobs)
        {
            bool terminal;
            lock (_gate)
            {
                terminal = JobStatuses.IsTerminal(job.Status);
                if (!terminal) job.Reported = true;
            }
            if (terminal) continue;
            try
            {
                job.Cancel(reason);
                lock (_gate) job.Status = JobStatus.Stopping;
                NotifyChanged(job.OwnerSession);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn($"jobs: cancel of {job.Id} threw during teardown; job record forced failed and work may be orphaned: {error.Message}");
                Settle(job, new JobOutcome(JobStatus.Failed, Detail: $"cancel threw during teardown; work may be orphaned: {error.Message}"));
            }
        }
    }
}
