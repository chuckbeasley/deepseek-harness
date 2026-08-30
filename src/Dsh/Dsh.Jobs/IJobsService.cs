namespace Dsh.Jobs;

/// <summary>
/// Service Definition of the background-job capability seam (ctx.jobs). It owns the contract for
/// job ids, session-scoped access, lifecycle state, completion listeners, and consuming output
/// reads while producers retain their execution resources. The process-local registry lives in
/// <see cref="LocalJobsProvider"/>. Port of <c>@deepseek-ai/dsh-jobs</c>.
///
/// Port deviations from the TS seam: ownership is the owner's session id (a string) rather than a
/// live Agent, so owner disposal is not wired to agent teardown — service disposal cancels and
/// awaits running jobs instead. Completion listeners receive the owner session id (or null for
/// unowned work) rather than the Agent instance.
/// </summary>
public interface IJobsService
{
    /// <summary>
    /// Preflight access, validation, and admission before starting and atomically registering work.
    /// Any preflight rejection leaves no job id or execution resource; a throwing starter leaves
    /// nothing registered. Settlement records the outcome, notifies listeners, and releases waiters.
    /// </summary>
    /// <param name="spec">job identity, owner session, and synchronous starter.</param>
    /// <returns>the registry-issued <c>&lt;kind&gt;-N</c> id.</returns>
    JobId Start(JobStartRequest spec);

    /// <summary>
    /// List caller-owned and unowned jobs in registration order without exposing another session's
    /// labels. A null caller sees only unowned jobs.
    /// </summary>
    /// <returns>fresh snapshots.</returns>
    IReadOnlyList<JobSnapshot> List(string? callerSession = null);

    /// <summary>
    /// Return a non-consuming snapshot without changing its read cursor or notice state.
    /// </summary>
    /// <exception cref="InvalidOperationException">for an unknown or foreign job.</exception>
    JobSnapshot Get(JobId id, string? callerSession = null);

    /// <summary>
    /// Read the next stream delta (consuming — never re-delivered), or the idempotent final output
    /// after settlement for final-output jobs. A terminal read marks the job reported. Reads drop
    /// unread stream output: the cursor is lossy by construction.
    /// </summary>
    /// <exception cref="InvalidOperationException">for an unknown or foreign job.</exception>
    JobRead Read(JobId id, string? callerSession = null);

    /// <summary>
    /// Request cancellation, then mark the job stopping and reported. A producer throw propagates
    /// without changing job state.
    /// </summary>
    /// <exception cref="InvalidOperationException">for an unknown or foreign job.</exception>
    JobKillResult Kill(JobId id, string? callerSession = null, string? reason = null);

    /// <summary>
    /// Wait for settlement or timeout without cancelling the job. Caller cancellation rejects only
    /// while the job is live; after settlement the terminal snapshot wins.
    /// </summary>
    /// <exception cref="InvalidOperationException">for invalid, unknown, or foreign input.</exception>
    Task<JobSnapshot> WaitAsync(JobId id, int timeoutMs, string? callerSession = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register an effect-scoped completion listener. It receives each terminal snapshot and the
    /// exact owner session id (or null for unowned work); each listener is contained. No listener
    /// runs after service disposal.
    /// </summary>
    /// <returns>a disposer that unregisters the listener.</returns>
    IDisposable OnJobDone(Action<JobSnapshot, string?> listener);

    /// <summary>
    /// Register an effect-scoped observer of visible-set changes. It fires after every commit that
    /// changes what <see cref="List"/> returns for that owner — registration, every stopping
    /// transition, settlement, and the emptying that service disposal commits — so an observer
    /// re-reads rather than accumulating deltas. An <c>undefined</c>-equivalent owner (null) means
    /// an unowned job changed and every caller's set did. Listeners are contained and never awaited.
    /// </summary>
    /// <returns>a disposer that unregisters the observer.</returns>
    IDisposable OnJobsChanged(Action<string?> listener);
}
