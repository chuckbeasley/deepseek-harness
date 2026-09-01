using System.Text.Json;

namespace Harness.Guard.Tests;

/// <summary>
/// Tool-timeout-policy coverage (ported from the TS spec): delegation for unconfigured tools, the
/// structured TOOL_TIMEOUT substitution when a budget expires, the default budget for unlisted
/// tools, the cap clamping every effective budget, the budget-resolution surface, and fail-loud
/// config validation. Driven through the real tool runtime against cooperative tools; no network.
/// </summary>
public static class TimeoutPolicyTests
{
    /// <summary>A tool that returns immediately with its own value.</summary>
    private static ToolDefinition FastTool(string name) => new(
        name, $"fast {name}",
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["ok"] = true })));

    /// <summary>A tool that settles only after a long sleep (longer than every test budget).</summary>
    private static ToolDefinition SlowTool(string name) => new(
        name, $"slow {name}",
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        async (_, _) =>
        {
            await Task.Delay(1000);
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["ok"] = true });
        });

    /// <summary>Boot a bare context with the tool registry and the timeout policy.</summary>
    private static (Context Ctx, ToolRuntime Tools) Boot(ToolTimeoutConfig? config = null)
    {
        var ctx = new Context();
        var tools = new ToolRuntime(ctx);
        _ = new ToolTimeoutPolicy(ctx, config);
        return (ctx, tools);
    }

    private static ToolExecutionInput Call(string id, string name)
        => new(new ToolCallId(id), name, JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), CancellationToken.None);

    public static void DelegatesUnconfiguredToolsUnchanged()
    {
        var (ctx, tools) = Boot();
        try
        {
            tools.Register(FastTool("probe"));
            var upstream = new CancellationTokenSource().Token;
            var input = new ToolExecutionInput(new ToolCallId("c1"), "probe",
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), upstream);

            var result = tools.ExecuteAsync(input, CancellationToken.None).GetAwaiter().GetResult();

            Assert.False(result.IsError, "a tool with no budget must run to its own result");
            var success = Assert.IsType<ToolExecutionSuccess>(result, "the result must be the tool's own success");
            Assert.True(success.Value.GetProperty("ok").GetBoolean(), "the tool's own value must be preserved");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void FastBudgetedToolKeepsItsOwnResult()
    {
        var (ctx, tools) = Boot(new ToolTimeoutConfig
        {
            PerToolTimeoutMs = new Dictionary<string, long> { ["fast"] = 10_000 },
        });
        try
        {
            tools.Register(FastTool("fast"));

            var result = tools.ExecuteAsync(Call("c1", "fast"), CancellationToken.None).GetAwaiter().GetResult();

            Assert.False(result.IsError, "a fast budgeted tool must keep its own result");
            var success = Assert.IsType<ToolExecutionSuccess>(result, "no timeout may fire for a fast call");
            Assert.True(success.Value.GetProperty("ok").GetBoolean(), "the tool's own value must be preserved");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void SlowToolIsReplacedWithTheTimeoutResult()
    {
        var (ctx, tools) = Boot(new ToolTimeoutConfig
        {
            PerToolTimeoutMs = new Dictionary<string, long> { ["slow"] = 50 },
        });
        try
        {
            tools.Register(SlowTool("slow"));

            var result = tools.ExecuteAsync(Call("c1", "slow"), CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.IsError, "a budget that expires must substitute the timeout result");
            var failure = Assert.IsType<ToolExecutionFailure>(result, "the substituted result must be a failure");
            Assert.Equal("tool call timed out after 50ms", failure.Error.Message, "the message must render the elapsed budget");
            Assert.Equal("ToolTimeoutError", failure.Error.Name, "the error name must be the owned ToolTimeoutError");
            Assert.Equal(ToolTimeoutPolicy.TimeoutCode, failure.Error.Code, "the error code must be the owned TOOL_TIMEOUT");
            var text = Assert.IsType<TextBlock>(failure.Content[0], "the model-facing content must be one text block");
            Assert.Equal("Error: tool call timed out after 50ms", text.Text, "the content must carry the Error: prefix");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void DefaultTimeoutAppliesToUnlistedTools()
    {
        var (ctx, tools) = Boot(new ToolTimeoutConfig { DefaultTimeoutMs = 50 });
        try
        {
            tools.Register(SlowTool("slow"));

            var result = tools.ExecuteAsync(Call("c1", "slow"), CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.IsError, "the default budget must arm tools without a per-tool entry");
            var failure = Assert.IsType<ToolExecutionFailure>(result, "the substituted result must be a failure");
            Assert.Equal("tool call timed out after 50ms", failure.Error.Message, "the default budget must be the rendered budget");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void CapClampsTheEffectiveBudget()
    {
        var (ctx, tools) = Boot(new ToolTimeoutConfig
        {
            PerToolTimeoutMs = new Dictionary<string, long> { ["slow"] = 10_000 },
            CapMs = 50,
        });
        try
        {
            tools.Register(SlowTool("slow"));

            var result = tools.ExecuteAsync(Call("c1", "slow"), CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.IsError, "the capped budget must still expire");
            var failure = Assert.IsType<ToolExecutionFailure>(result, "the substituted result must be a failure");
            Assert.Equal("tool call timed out after 50ms", failure.Error.Message, "the rendered budget must be the clamped cap, not the declared entry");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void TimeoutMsForResolvesBudgets()
    {
        var (ctx, _) = Boot(new ToolTimeoutConfig
        {
            DefaultTimeoutMs = 100,
            CapMs = 1000,
            PerToolTimeoutMs = new Dictionary<string, long> { ["probe"] = 500, ["huge"] = 5000 },
        });
        try
        {
            var policy = ctx.Get<ToolTimeoutPolicy>(ToolTimeoutPolicy.ServiceKey)!;
            Assert.Equal(500, policy.TimeoutMsFor("probe"), "a per-tool entry must win over the default");
            Assert.Equal(1000, policy.TimeoutMsFor("huge"), "the cap must clamp a per-tool entry above it");
            Assert.Equal(100, policy.TimeoutMsFor("other"), "the default must arm an unlisted tool");
            Assert.Equal("timeout-policy", ((IGuardService)policy).Name, "the guard must expose its stable name");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void ReminderGuardArmsNoTimeout()
    {
        using var ctx = new Context();
        var reminder = new RepeatToolReminderGuard(ctx);

        Assert.Null(reminder.TimeoutMsFor("probe"), "the repeat reminder must arm no deadline for any tool");
        Assert.Equal("repeat-tool-reminder", ((IGuardService)reminder).Name, "the guard must expose its stable name");
    }

    public static void ConfigValidationFailsLoud()
    {
        using var ctx = new Context();
        Assert.Throws<ArgumentException>(
            () => new ToolTimeoutPolicy(ctx, new ToolTimeoutConfig { DefaultTimeoutMs = -1 }),
            "a negative default budget must be refused");
        Assert.Throws<ArgumentException>(
            () => new ToolTimeoutPolicy(ctx, new ToolTimeoutConfig { CapMs = -1 }),
            "a negative cap must be refused");
        Assert.Throws<ArgumentException>(
            () => new ToolTimeoutPolicy(ctx, new ToolTimeoutConfig { PerToolTimeoutMs = new Dictionary<string, long> { ["slow"] = 0 } }),
            "a zero per-tool budget must be refused");
        Assert.Null(ctx.Get<ToolTimeoutPolicy>(ToolTimeoutPolicy.ServiceKey), "a refused config must not leave the guard installed");
    }
}
