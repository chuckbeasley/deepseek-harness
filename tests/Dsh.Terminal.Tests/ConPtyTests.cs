using Cordis.Core;
using Dsh.Terminal;

namespace Dsh.Terminal.Tests;

/// <summary>
/// The ConPTY backend (Windows-only; registered by Program.cs only on Windows): real TTY
/// semantics — echo, carriage-return submission, prompt-marker readiness, resize, session-exit
/// settlement, teardown, and the bounded scrollback.
/// </summary>
public static class ConPtyTests
{
    private static ConPtyTerminalProvider Provider(ConPtyConfig? config = null)
    {
        var ctx = new Context();
        return new ConPtyTerminalProvider(ctx, config);
    }

    private static string TestAssembly => typeof(ConPtyTests).Assembly.Location;

    public static async Task Open_SendsAndReadsOutput()
    {
        var service = Provider();
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType, "test"));
        var operation = session.StartSend(new TerminalSendRequest("echo hello-from-conpty", Submit: true));
        var result = await operation.Done.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Viewport.Contains("hello-from-conpty"), "the sent command's output lands in the viewport");
        Assert.Equal(TerminalWaitReason.StdinRead, result.WaitReason, "the prompt marker settles the send");
        Assert.True(service.List().Any(snapshot => snapshot.SessionId == session.SessionId), "the session appears in the registry list");
        await session.CloseAsync("test done");
        Assert.True(session.Status() is TerminalSessionStatus.Exited, "the session reports exited after close");
        Assert.False(service.List().Any(snapshot => snapshot.SessionId == session.SessionId), "closed sessions leave the registry");
    }

    public static async Task Submit_WritesCarriageReturn_ByteExact()
    {
        var service = Provider(new ConPtyConfig(ShellPath: TestAssembly, TimeoutMs: 10000));
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        var submit = session.StartSend(new TerminalSendRequest("abc", Submit: true));
        var submitResult = await submit.Done.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(submitResult.Viewport.Contains("ECHO_HEX:6162630D", StringComparison.OrdinalIgnoreCase),
            $"submitting appends CR (0D), got: {submitResult.Viewport}");
        var bare = session.StartSend(new TerminalSendRequest("def", Submit: false));
        var bareResult = await bare.Done.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(bareResult.Viewport.Contains("ECHO_HEX:646566", StringComparison.OrdinalIgnoreCase),
            $"a bare send appends nothing, got: {bareResult.Viewport}");
        await session.CloseAsync("done");
    }

    public static async Task Read_ReturnsTheRetainedScrollback()
    {
        var service = Provider();
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        await session.StartSend(new TerminalSendRequest("echo line-one", Submit: true)).Done.WaitAsync(TimeSpan.FromSeconds(30));
        await session.StartSend(new TerminalSendRequest("echo line-two", Submit: true)).Done.WaitAsync(TimeSpan.FromSeconds(30));
        var read = session.Read(new TerminalReadRequest());
        Assert.True(read.Text.Contains("line-one"), "the scrollback holds the first line");
        Assert.True(read.Text.Contains("line-two"), "the scrollback holds the second line");
        Assert.True(read.TotalLines >= 2, "the line count covers the retained output");
        await session.CloseAsync("done");
    }

    public static async Task SessionExit_SettlesTheActiveSend()
    {
        var service = Provider();
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        var operation = session.StartSend(new TerminalSendRequest("exit", Submit: true));
        var result = await operation.Done.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TerminalWaitReason.SessionExit, result.WaitReason, "the shell exit settles the pending send");
        Assert.True(session.Status() is TerminalSessionStatus.Exited, "the session reports exited");
        await session.CloseAsync("done");
    }

    public static async Task Close_KillsTheChildTree()
    {
        var service = Provider();
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        var pid = session.Pid;
        Assert.NotNull(pid, "the session exposes the shell pid");
        await session.CloseAsync("closed by test");
        Assert.True(session.Status() is TerminalSessionStatus.Exited, "close settles the session exited");
        await Assert.WaitUntilAsync(() => !ProcessAlive(pid!.Value), 10000, "the shell tree is gone after close");
    }

    public static async Task Timeout_OnSilentHang()
    {
        var service = Provider(new ConPtyConfig(ShellPath: TestAssembly, TimeoutMs: 1500, IdleSilenceMs: 10000));
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        var operation = session.StartSend(new TerminalSendRequest("SILENT", Submit: true));
        var result = await operation.Done.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TerminalWaitReason.Timeout, result.WaitReason, "a silent child settles the send on the absolute timeout");
        await session.CloseAsync("done");
    }

    public static async Task Resize_RoundTrip()
    {
        var service = Provider(new ConPtyConfig(ShellPath: TestAssembly, Cols: 80, Rows: 24, TimeoutMs: 10000));
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        var conPty = (ConPtySession)session;
        await Assert.WaitUntilAsync(() => SizeOf(session) == "80x24", 20000, $"the initial size is 80x24, got {SizeOf(session)}");
        conPty.Resize(120, 30);
        await Assert.WaitUntilAsync(() => SizeOf(session) == "120x30", 20000, $"the resized size is 120x30, got {SizeOf(session)}");
        await session.CloseAsync("done");
    }

    public static async Task ConcurrentSend_ReturnsTheActiveSend()
    {
        var service = Provider(new ConPtyConfig(ShellPath: TestAssembly, TimeoutMs: 10000));
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        var first = session.StartSend(new TerminalSendRequest("SILENT", Submit: true));
        var second = session.StartSend(new TerminalSendRequest("SILENT", Submit: true));
        Assert.True(ReferenceEquals(first, second), "a second send while one is active returns the active send");
        first.Cancel();
        await session.CloseAsync("done");
    }

    public static async Task Dispose_ClosesLiveSessions()
    {
        var ctx = new Context();
        var service = new ConPtyTerminalProvider(ctx);
        var session = await service.OpenAsync(new TerminalOpenRequest(ConPtyTerminalProvider.BackendType));
        ctx.Dispose();
        Assert.True(session.Status() is TerminalSessionStatus.Exited, "context disposal closes the live session");
    }

    private static string SizeOf(ITerminalSession session)
    {
        // Re-probe until the fake answers with the current console size.
        var deadline = Environment.TickCount64 + 30000;
        string? size = null;
        while (Environment.TickCount64 < deadline)
        {
            var probe = session.StartSend(new TerminalSendRequest("SIZE", Submit: true));
            _ = probe.Done.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var viewport = probe.ReadOutput().Delta;
            var marker = "SIZE:";
            var index = viewport.LastIndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var candidate = viewport[(index + marker.Length)..];
                var end = candidate.IndexOf('\n');
                if (end >= 0) candidate = candidate[..end];
                size = candidate.Trim();
                if (size.Length > 0) return size;
            }
        }
        return size ?? "";
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
