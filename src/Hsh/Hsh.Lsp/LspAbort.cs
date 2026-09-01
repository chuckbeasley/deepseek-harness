namespace Harness.Lsp;

/// <summary>
/// Shared cancellation helpers (port of <c>abort.ts</c>): reason classification for an aborted token,
/// pre-flight checks, and awaiting work while a query signal may abandon its wait. The underlying work
/// keeps its own handlers and continues to its owner-defined quiescence boundary.
/// </summary>
public static class LspAbort
{
    private static readonly object Gate = new();
    private static readonly List<(CancellationToken Token, Exception Reason)> Reasons = new();

    /// <summary>Attach a classified abort reason to a token source; <see cref="AbortError"/> surfaces it.</summary>
    /// <param name="source">the token source whose cancellation carries <paramref name="reason"/>.</param>
    /// <param name="reason">the exception to surface when the token fires.</param>
    public static void SetReason(CancellationTokenSource source, Exception reason)
    {
        lock (Gate) Reasons.Add((source.Token, reason));
    }

    /// <summary>
    /// Build a deadline token source whose expiry classifies as <paramref name="deadlineName"/> (the
    /// harness deadline naming, for example <c>LSP_CANCEL_GRACE</c> or <c>LSP_SHUTDOWN</c>).
    /// </summary>
    /// <param name="deadlineName">the deadline's stable name.</param>
    /// <param name="milliseconds">the deadline budget in milliseconds.</param>
    /// <returns>a token source that fires after <paramref name="milliseconds"/>.</returns>
    public static CancellationTokenSource Deadline(string deadlineName, int milliseconds)
    {
        var source = new CancellationTokenSource();
        source.CancelAfter(milliseconds);
        lock (Gate) Reasons.Add((source.Token, new OperationCanceledException($"{deadlineName} deadline exceeded", source.Token)));
        return source;
    }

    /// <summary>Build the classified abort error for a fired token: an attached reason, else the generic abort.</summary>
    /// <param name="ct">the aborted token.</param>
    /// <param name="deadlineName">optional deadline name; when set the expiry classifies as that named timeout.</param>
    /// <returns>the reason exception to throw.</returns>
    public static Exception AbortError(CancellationToken ct, string? deadlineName = null)
    {
        if (deadlineName is not null) return new OperationCanceledException($"{deadlineName} deadline exceeded", ct);
        lock (Gate)
        {
            foreach (var (token, reason) in Reasons)
            {
                if (token == ct) return reason;
            }
        }
        return new OperationCanceledException("LSP query aborted", ct);
    }

    /// <summary>Throw the token's classified abort error when it has already fired (pre-flight check).</summary>
    /// <param name="ct">the optional query cancellation token.</param>
    public static void ThrowIfAborted(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) throw AbortError(ct);
    }

    /// <summary>Await work while allowing a query signal to abandon its wait.</summary>
    /// <param name="work">the owned asynchronous work.</param>
    /// <param name="ct">optional query cancellation.</param>
    /// <param name="deadlineName">optional deadline name for timeout classification.</param>
    /// <returns>the work result, or a rejection carrying the classified abort reason.</returns>
    public static async Task<T> Abortable<T>(Task<T> work, CancellationToken ct, string? deadlineName = null)
    {
        if (!ct.CanBeCanceled) return await work;
        if (ct.IsCancellationRequested) throw AbortError(ct, deadlineName);
        try
        {
            return await work.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw AbortError(ct, deadlineName);
        }
    }

    /// <summary>Await work while allowing a query signal to abandon its wait (non-generic form).</summary>
    /// <param name="work">the owned asynchronous work.</param>
    /// <param name="ct">optional query cancellation.</param>
    /// <param name="deadlineName">optional deadline name for timeout classification.</param>
    public static async Task Abortable(Task work, CancellationToken ct, string? deadlineName = null)
    {
        if (!ct.CanBeCanceled) { await work; return; }
        if (ct.IsCancellationRequested) throw AbortError(ct, deadlineName);
        try
        {
            await work.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw AbortError(ct, deadlineName);
        }
    }
}
