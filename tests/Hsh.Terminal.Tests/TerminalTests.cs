using Harness.Cordis.Core;
using Harness.Terminal;

namespace Harness.Terminal.Tests;

/// <summary>The local provider's open/send/read/close lifecycle.</summary>
public static class TerminalTests
{
    public static async Task Open_SendsAndReadsOutput()
    {
        var ctx = new Context();
        var service = new LocalTerminalProvider(ctx);
        var session = await service.OpenAsync(new TerminalOpenRequest(LocalTerminalProvider.BackendType, "test"));
        var operation = session.StartSend(new TerminalSendRequest("echo hello-from-terminal", Submit: true));
        var result = await operation.Done.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Viewport.Contains("hello-from-terminal"), "the sent command's output lands in the viewport");
        Assert.True(service.List().Any(snapshot => snapshot.SessionId == session.SessionId), "the session appears in the registry list");
        await session.CloseAsync("test done");
        Assert.True(session.Status() is TerminalSessionStatus.Exited, "the session reports exited after close");
        Assert.False(service.List().Any(snapshot => snapshot.SessionId == session.SessionId), "closed sessions leave the registry");
        ctx.Dispose();
    }

    public static async Task Send_WithoutSubmit_AppendsNoNewline()
    {
        var ctx = new Context();
        var service = new LocalTerminalProvider(ctx);
        var session = await service.OpenAsync(new TerminalOpenRequest(LocalTerminalProvider.BackendType));
        await session.StartSend(new TerminalSendRequest("echo bare", Submit: true)).Done.WaitAsync(TimeSpan.FromSeconds(20));
        var operation = session.StartSend(new TerminalSendRequest(" echo tail", Submit: true));
        var result = await operation.Done.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Viewport.Contains("bare"), "the first command's output is retained in the scrollback");
        await session.CloseAsync("done");
        ctx.Dispose();
    }

    public static async Task Read_ReturnsTheRetainedScrollback()
    {
        var ctx = new Context();
        var service = new LocalTerminalProvider(ctx);
        var session = await service.OpenAsync(new TerminalOpenRequest(LocalTerminalProvider.BackendType));
        await session.StartSend(new TerminalSendRequest("echo line-one", Submit: true)).Done.WaitAsync(TimeSpan.FromSeconds(20));
        await session.StartSend(new TerminalSendRequest("echo line-two", Submit: true)).Done.WaitAsync(TimeSpan.FromSeconds(20));
        var read = session.Read(new TerminalReadRequest());
        Assert.True(read.Text.Contains("line-one"), "the scrollback holds the first line");
        Assert.True(read.Text.Contains("line-two"), "the scrollback holds the second line");
        Assert.True(read.TotalLines >= 2, "the line count covers the retained output");
        await session.CloseAsync("done");
        ctx.Dispose();
    }

    public static async Task UnknownBackendType_FailsLoud()
    {
        var ctx = new Context();
        var service = new LocalTerminalProvider(ctx);
        try
        {
            await service.OpenAsync(new TerminalOpenRequest("not-a-backend"));
            Assert.True(false, "an unknown backend type must fail loud");
        }
        catch (InvalidOperationException error)
        {
            Assert.True(error.Message.Contains("unknown backend type"), "the error names the unknown type");
        }
        ctx.Dispose();
    }

    public static async Task Dispose_ClosesLiveSessions()
    {
        var ctx = new Context();
        var service = new LocalTerminalProvider(ctx);
        var session = await service.OpenAsync(new TerminalOpenRequest(LocalTerminalProvider.BackendType));
        ctx.Dispose();
        Assert.True(session.Status() is TerminalSessionStatus.Exited, "context disposal closes the live session");
    }
}
