namespace Dsh.Compaction;

/// <summary>
/// Compaction Service Definition (ctx.compaction): providers decide when to compact and replace a
/// history range with one summary checkpoint. Port of <c>@deepseek-ai/dsh-compaction</c>'s
/// CompactionEngine reduced to the region transaction: the pre-step pressure listener, the
/// tool-result pruner, and the manual idle-session compactNow/runMaintenance path are deferred
/// and named but not ported. The explicit resolve(request): spec step is the port's budget model
/// — <see cref="Resolve"/> validates the request and produces the concrete token budgets plus the
/// selected region; <see cref="Compact"/> resolves and runs the durable transaction; the basic
/// provider additionally exposes the overflow recovery path (see
/// <see cref="BasicCompactionProvider.CompactOverflowAsync"/>).
/// </summary>
public interface ICompactionService
{
    /// <summary>Explicit resolve(request): spec step — validate the request and produce the concrete budget and region.</summary>
    /// <exception cref="CompactionConfigError">on an invalid policy field.</exception>
    /// <exception cref="TargetPressureConfigError">on an invalid context capacity or a retention budget that is not below the pressure threshold.</exception>
    CompactionSpec Resolve(CompactionRequest request);

    /// <summary>Resolve and run one compaction transaction over the request's session.</summary>
    /// <returns>the durable result, or null when no safe compactable region exists.</returns>
    /// <exception cref="ManualCompactionError">with <see cref="ManualCompactionErrorCode.Busy"/> when the session compaction lock is already active.</exception>
    CompactionResult? Compact(CompactionRequest request);

    /// <summary>Select the head-anchored compactable region for one session under a retained-tail token budget.</summary>
    /// <returns>the inclusive positional region, or null when nothing compactable remains.</returns>
    SurfaceSelection? SelectRange(Dsh.Session.Session session, long retainTokens);
}
