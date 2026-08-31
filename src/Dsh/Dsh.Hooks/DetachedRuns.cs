namespace Dsh.Hooks;

/// <summary>
/// Quiescence tracking for emit-shaped hook runs that no extension point awaits (port of the TS
/// <c>createDetachedRuns</c>). Bridges track the run plus its continuation, pass the tracker token
/// into execution, and drain on disposal so no process or late callback outlives the fiber.
/// </summary>
public sealed class DetachedRuns
{
    private readonly HashSet<Task> _inflight = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// The cancellation token every tracked run must hand to <see cref="HookRunner.RunHook"/>.
    /// <see cref="DrainAsync"/> fires it so a still-running hook process is killed rather than
    /// awaited out to its timeout (default 10 minutes).
    /// </summary>
    public CancellationToken Signal => _cts.Token;

    /// <summary>
    /// Register one detached run until it settles. Pass the FULL chain — the hook run and its
    /// continuation/error handler — so <see cref="DrainAsync"/> waits for the side effects (an
    /// inject, a warn), not just the process exit. A rejected chain is absorbed here (settlement
    /// bookkeeping only); rejection handling is still the caller's job.
    /// </summary>
    /// <param name="run">the detached run chain to hold until settled.</param>
    public void Track(Task run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_inflight)
        {
            _inflight.Add(run);
        }
        _ = run.ContinueWith(_ =>
        {
            lock (_inflight)
            {
                _inflight.Remove(run);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Abort <see cref="Signal"/>, then resolve once every tracked chain has settled — including
    /// chains tracked while the drain is in progress. A run tracked AFTER drain resolves is not
    /// awaited by anyone — by then the bridge's listeners are disposed, so nothing can start one.
    /// </summary>
    /// <returns>a task resolving when all tracked runs have settled.</returns>
    public async Task DrainAsync()
    {
        _cts.Cancel();
        // Re-check after each wave: a chain can be tracked while a prior wave is settling; loop
        // until the registry is observed empty.
        while (true)
        {
            Task[] wave;
            lock (_inflight)
            {
                if (_inflight.Count == 0) return;
                wave = _inflight.ToArray();
            }
            try
            {
                await Task.WhenAll(wave);
            }
            catch (Exception)
            {
                // Settlement bookkeeping only; the caller owns rejection handling.
            }
        }
    }
}
