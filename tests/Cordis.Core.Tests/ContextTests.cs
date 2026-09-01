using Harness.Cordis.Core;
using Xunit;

namespace Harness.Cordis.Core.Tests;

public class ContextTests
{
    [Fact]
    public void Logger_Warn_IsCapturedByTheRingBuffer()
    {
        using var ctx = new Context();

        ctx.Logger.Warn("observer failed");

        var message = Assert.Single(ctx.Logger.Buffer);
        Assert.Equal("warn", message.Type);
        Assert.Equal("observer failed", Assert.IsType<string>(message.Args[0]));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var ctx = new Context();
        await ctx.DisposeAsync();
        await ctx.DisposeAsync();
        Assert.True(ctx.IsDisposed);
    }

    [Fact]
    public async Task On_AfterDispose_ThrowsInactiveEffect()
    {
        var ctx = new Context();
        await ctx.DisposeAsync();

        Assert.Throws<CordisError>(() => ctx.On("test/x", new Action(() => { })));
    }

    [Fact]
    public async Task Set_AfterDispose_ThrowsInactiveEffect()
    {
        var ctx = new Context();
        await ctx.DisposeAsync();

        Assert.Throws<CordisError>(() => ctx.Set("late", new object()));
    }

    [Fact]
    public void Emit_ReportsDispatchThroughInternalDispatch()
    {
        using var ctx = new Context();
        var seen = new List<object?>();
        ctx.On("internal/dispatch", new Action<string, string, object?[]>((mode, name, args) => seen.Add((mode, name))));

        ctx.Emit("test/event", 1, 2);

        Assert.Contains(("emit", "test/event"), seen);
    }
}
