namespace Harness.AgentLoop;

/// <summary>
/// Agent-loop plugin configuration. The declarative boot array of the TS Config belongs to the
/// Phase 3 boot composition; this port carries the deployment-wide scheduler cap.
/// </summary>
public sealed record AgentLoopConfig
{
    /// <summary>
    /// Maximum parallel-safe calls in flight per agent step. 1 is serial; omission defaults to
    /// <see cref="AgentLoopConstants.DefaultMaxParallelToolCalls"/>. The C# scheduler executes
    /// calls in model order today (pool overlap and exclusive-barrier reclassification arrive
    /// with a later phase), but the cap is resolved and validated at this configuration boundary
    /// so a misconfigured value fails loud at load.
    /// </summary>
    public int? MaxParallelToolCalls { get; init; }
}

/// <summary>Configuration resolution (port of resolveMaxParallelToolCalls).</summary>
public static class AgentLoopConfigResolver
{
    /// <summary>Resolve the deployment-wide scheduler cap at the owning configuration boundary.</summary>
    public static int ResolveMaxParallelToolCalls(int? value)
    {
        var cap = value ?? AgentLoopConstants.DefaultMaxParallelToolCalls;
        if (cap < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), cap, "maxParallelToolCalls must be a positive integer");
        }
        return cap;
    }
}
