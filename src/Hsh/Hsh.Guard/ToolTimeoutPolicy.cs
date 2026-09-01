using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Tools;

namespace Harness.Guard;

/// <summary>
/// Per-tool timeout configuration (port of the TS tool-declared <c>timeoutMs</c> budget, moved
/// into policy config because the C# tool vocabulary carries no timeout field). A tool with an
/// entry (or covered by the default) is armed with a deadline; the cap clamps every effective
/// budget. Misconfiguration fails loud at <see cref="ToolTimeoutPolicy"/> construction.
/// </summary>
public sealed record ToolTimeoutConfig
{
    /// <summary>Budget applied to a tool with no per-tool entry; 0 (the default) arms no default deadline.</summary>
    public long DefaultTimeoutMs { get; init; }

    /// <summary>Ceiling every effective budget is clamped to; 0 (the default) applies no cap.</summary>
    public long CapMs { get; init; }

    /// <summary>Per-tool budgets in milliseconds; every entry must be >= 1.</summary>
    public IReadOnlyDictionary<string, long> PerToolTimeoutMs { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// Cooperative tool-call timeout enforcer (port of the TS timeout-policy plugin): a tool armed
/// with a budget is executed under a deadline on the <c>tools/execute</c> waterfall, and a budget
/// that expires substitutes the structured <see cref="TimeoutCode"/> result. A tool with no
/// budget is delegated unchanged and its cancellation token is never touched. The C# port cannot
/// swap the caller's token on the immutable <see cref="ToolRunContext"/>, so the deadline is
/// enforced by racing the downstream result; a tool that ignores its own cancellation keeps
/// running and its late result is discarded (documented deviation — the TS swaps
/// <c>exec.signal</c> so the cooperative tool settles).
/// </summary>
public sealed class ToolTimeoutPolicy : Service, IGuardService
{
    /// <summary>The stable guard name.</summary>
    public const string GuardName = "timeout-policy";

    /// <summary>The service key this guard registers under.</summary>
    public const string ServiceKey = "guard:timeout-policy";

    /// <summary>
    /// The code owned by this guard, used as the structured error <c>code</c> on the replacement
    /// tool result so a retry/sandbox plugin and replay can route on it.
    /// </summary>
    public const string TimeoutCode = "TOOL_TIMEOUT";

    private readonly ToolTimeoutConfig _config;

    /// <summary>
    /// Create and install the guard as <c>guard:timeout-policy</c>; validation fails loud here.
    /// </summary>
    /// <param name="ctx">the owner context whose <c>tools/execute</c> waterfall is wrapped.</param>
    /// <param name="config">the per-tool timeout policy; absent fields take the documented defaults.</param>
    public ToolTimeoutPolicy(Context ctx, ToolTimeoutConfig? config = null)
        : base(ctx, ServiceKey)
    {
        try
        {
            _config = config ?? new ToolTimeoutConfig();
            Validate(_config);
            Ctx.On("tools/execute",
                new Func<ToolRunContext, Func<Task<ToolExecutionResult>>, Task<ToolExecutionResult>>(WrapAsync));
        }
        catch
        {
            // Fail loud and leak nothing: unregister the service entry the base constructor just
            // created so a refused config leaves no half-installed guard behind.
            Ctx.Remove(ServiceKey);
            throw;
        }
    }

    /// <inheritdoc />
    public long? TimeoutMsFor(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        long? budget = _config.PerToolTimeoutMs.TryGetValue(toolName, out var perTool)
            ? perTool
            : _config.DefaultTimeoutMs > 0 ? _config.DefaultTimeoutMs : null;
        if (budget is null) return null;
        if (_config.CapMs > 0 && budget.Value > _config.CapMs) return _config.CapMs;
        return budget;
    }

    /// <inheritdoc />
    string IGuardService.Name => GuardName;

    /// <summary>
    /// The structured result substituted when this guard's deadline wins. <see cref="ToolExecutionResult.Content"/>
    /// is the model-facing message; <see cref="ToolFailure.Code"/> is <see cref="TimeoutCode"/>.
    /// </summary>
    /// <param name="timeoutMs">the elapsed budget, rendered into the model-facing message.</param>
    public static ToolExecutionResult TimeoutResult(long timeoutMs)
    {
        var message = $"tool call timed out after {timeoutMs}ms";
        return new ToolExecutionFailure(
            new ToolFailure(message, "ToolTimeoutError", TimeoutCode),
            new ContentBlock[] { new TextBlock($"Error: {message}") });
    }

    /// <summary>Run the downstream chain under the tool's budget, substituting the timeout result when the deadline wins.</summary>
    private async Task<ToolExecutionResult> WrapAsync(ToolRunContext exec, Func<Task<ToolExecutionResult>> next)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentNullException.ThrowIfNull(next);
        var budget = TimeoutMsFor(exec.Name);
        // A tool with no budget: no deadline, delegate unchanged.
        if (budget is null) return await next();
        var run = next();
        var deadline = Task.Delay(TimeSpan.FromMilliseconds(budget.Value));
        var winner = await Task.WhenAny(run, deadline);
        if (winner == run) return await run;
        // The deadline won; observe a late failure so it cannot surface as an unobserved task
        // exception, and substitute the structured timeout result the model sees.
        _ = run.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return TimeoutResult(budget.Value);
    }

    /// <summary>Validate the policy fail loud: no negative default or cap, every per-tool entry >= 1.</summary>
    private static void Validate(ToolTimeoutConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.DefaultTimeoutMs < 0)
        {
            throw new ArgumentException($"timeout-policy: invalid DefaultTimeoutMs {config.DefaultTimeoutMs} — must be >= 0", nameof(config));
        }
        if (config.CapMs < 0)
        {
            throw new ArgumentException($"timeout-policy: invalid CapMs {config.CapMs} — must be >= 0", nameof(config));
        }
        foreach (var (tool, budget) in config.PerToolTimeoutMs)
        {
            if (budget < 1)
            {
                throw new ArgumentException(
                    $"timeout-policy: invalid per-tool timeout {tool}={budget} — every timeout must be an integer >= 1",
                    nameof(config));
            }
        }
    }
}
