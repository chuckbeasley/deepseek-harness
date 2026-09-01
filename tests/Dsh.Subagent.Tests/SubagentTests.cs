using Cordis.Core;
using Dsh.Subagent;

namespace Dsh.Subagent.Tests;

/// <summary>The in-process provider's delegate/settle/cancel/teardown lifecycle.</summary>
public static class SubagentTests
{
    public static async Task Delegate_RunsAndSettlesCompleted()
    {
        var ctx = new Context();
        var service = new InProcessSubagentProvider(ctx, (request, _) => Task.FromResult(new SubagentResult($"handled: {request.Task}")));
        var handle = service.Delegate(new SubagentRequest("write tests", "tests"));
        var result = await handle.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SubagentStatus.Completed, handle.Status, "the delegation completes");
        Assert.Equal("handled: write tests", result.Text, "the runner text returns");
        Assert.False(result.IsError, "a completed delegation is not an error");
        Assert.Equal(new SubagentId("subagent-1"), handle.Id, "ids mint in order");
        ctx.Dispose();
    }

    public static async Task Delegate_FailureSettlesFailedWithTheErrorText()
    {
        var ctx = new Context();
        var service = new InProcessSubagentProvider(ctx, (_, _) => throw new InvalidOperationException("boom"));
        var handle = service.Delegate(new SubagentRequest("fail"));
        var result = await handle.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SubagentStatus.Failed, handle.Status, "a throwing body fails the delegation");
        Assert.True(result.IsError, "the failed result is marked as an error");
        Assert.True(result.Text.Contains("boom"), "the error text carries the failure message");
        ctx.Dispose();
    }

    public static async Task Cancel_MarksCancelledAndSettles()
    {
        var ctx = new Context();
        var service = new InProcessSubagentProvider(ctx, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new SubagentResult("unreachable");
        });
        var handle = service.Delegate(new SubagentRequest("wait forever"));
        await Task.Delay(200);
        Assert.True(handle.Cancel(), "an unsettled delegation cancels");
        Assert.False(handle.Cancel(), "a second cancel is a no-op");
        var result = await handle.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SubagentStatus.Cancelled, handle.Status, "the delegation settles cancelled");
        ctx.Dispose();
    }

    public static async Task Teardown_CancelsLiveDelegations()
    {
        var ctx = new Context();
        var service = new InProcessSubagentProvider(ctx, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new SubagentResult("unreachable");
        });
        var handle = service.Delegate(new SubagentRequest("wait forever"));
        await Task.Delay(200);
        ctx.Dispose();
        Assert.Equal(SubagentStatus.Cancelled, handle.Status, "context disposal cancels the live delegation");
    }

    public static void EmptyTask_Throws()
    {
        var ctx = new Context();
        var service = new InProcessSubagentProvider(ctx);
        try
        {
            service.Delegate(new SubagentRequest("   "));
            Assert.True(false, "an empty task must throw");
        }
        catch (ArgumentException error)
        {
            Assert.True(error.Message.Contains("task"), "the error names the task field");
        }
        ctx.Dispose();
    }
}
