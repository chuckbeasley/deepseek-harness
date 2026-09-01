using Harness.Cordis.Core;
using Harness.Subprocess;

namespace Harness.Subprocess.Tests;

/// <summary>The local provider's spawn, collect, env, spill, and termination contracts.</summary>
public static class SubprocessTests
{
    private static ISubprocessService Boot(out Context ctx)
    {
        ctx = new Context();
        return new LocalSubprocessProvider(ctx);
    }

    private static SubprocessSpawnSpec Cmd(string command, SubprocessStdio? stdio = null)
        => new(
            new[] { "cmd.exe", "/d", "/s", "/c", command },
            Environment.CurrentDirectory,
            stdio ?? new SubprocessStdio(
                new IgnoreStdin(),
                new CollectOutput(new SubprocessCollect(65536)),
                new CollectOutput(new SubprocessCollect(65536))),
            5000);

    private static string ReadAll(ISubprocessOutputReader? reader)
        => reader?.ReadFrom(0).Text ?? string.Empty;

    public static async Task Collect_ReadsStdoutAndExitCode()
    {
        var service = Boot(out var ctx);
        using var _ = ctx;
        var handle = service.Spawn(Cmd("echo hello"));
        var outcome = await handle.Done;
        Assert.Equal(0, outcome.ExitCode, "the child exits 0");
        Assert.Equal("hello", ReadAll(handle.Collected.Stdout).TrimEnd(), "stdout is collected verbatim");
    }

    public static async Task NonZero_ExitCodePropagates()
    {
        var service = Boot(out var ctx);
        using var _ = ctx;
        var handle = service.Spawn(Cmd("exit 3"));
        var outcome = await handle.Done;
        Assert.Equal(3, outcome.ExitCode, "the child's exit code propagates");
    }

    public static async Task Env_MergesExplicitAndScrubsManagedFacts()
    {
        var old = Environment.GetEnvironmentVariable("DSH_SCRUB_TEST");
        Environment.SetEnvironmentVariable("DSH_SCRUB_TEST", "stale");
        try
        {
            var service = Boot(out var ctx);
            using var _ = ctx;
            var handle = service.Spawn(new SubprocessSpawnSpec(
                new[] { "cmd.exe", "/d", "/s", "/c", "echo [%DSH_SCRUB_TEST%][%FOO%]" },
                Environment.CurrentDirectory,
                new SubprocessStdio(new IgnoreStdin(), new CollectOutput(new SubprocessCollect(65536)), new CollectOutput(new SubprocessCollect(65536))),
                5000,
                Env: new Dictionary<string, string?> { ["FOO"] = "bar" }));
            var outcome = await handle.Done;
            Assert.Equal(0, outcome.ExitCode, "the child exits 0");
            // cmd renders an undefined variable literally, so the literal %DSH_SCRUB_TEST% IS the proof of the scrub.
Assert.Equal("[%DSH_SCRUB_TEST%][bar]", ReadAll(handle.Collected.Stdout).TrimEnd(), "the managed DSH_ fact is scrubbed and the explicit entry merges");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_SCRUB_TEST", old);
        }
    }

    public static async Task Collect_KeepsTheBoundedTailAndSpillsTheFullStream()
    {
        var service = Boot(out var ctx);
        using var _ = ctx;
        var line = new string('x', 100);
        var command = $"for /l %i in (1,1,100) do @echo {line}%i"; // ~10100 bytes
        var handle = service.Spawn(new SubprocessSpawnSpec(
            new[] { "cmd.exe", "/d", "/s", "/c", command },
            Environment.CurrentDirectory,
            new SubprocessStdio(new IgnoreStdin(), new CollectOutput(new SubprocessCollect(100, 100000)), new CollectOutput(new SubprocessCollect(65536))),
            5000));
        var outcome = await handle.Done;
        Assert.Equal(0, outcome.ExitCode, "the child exits 0");
        var read = handle.Collected.Stdout!.ReadFrom(0);
        Assert.True(read.Lossy, "a capped stream that overflowed is lossy from offset 0");
        Assert.Equal(100, read.Text.Length, "the retained text is exactly the tail cap");
        Assert.True(read.SpillPath is not null && File.Exists(read.SpillPath), "the complete stream spills to a file");
        var spilled = File.ReadAllText(read.SpillPath!);
        Assert.True(spilled.Length > 10000, "the spill holds the complete stream");
        File.Delete(read.SpillPath!);
    }

    public static async Task Terminate_KillsTheTreeAndSettlesDone()
    {
        var service = Boot(out var ctx);
        using var _ = ctx;
        var handle = service.Spawn(Cmd("ping -n 60 127.0.0.1 >nul"));
        await Task.Delay(500);
        handle.Terminate();
        var outcome = await handle.Done.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(outcome.ExitCode is null or not 0, "a killed child does not exit 0");
    }

    public static async Task Stdin_BatchWritesThenCloses()
    {
        var service = Boot(out var ctx);
        using var _ = ctx;
        var handle = service.Spawn(new SubprocessSpawnSpec(
            new[] { "cmd.exe", "/v:on", "/d", "/s", "/c", "set /p X=&echo got:!X!" },
            Environment.CurrentDirectory,
            new SubprocessStdio(new DataStdin("hello"), new CollectOutput(new SubprocessCollect(65536)), new CollectOutput(new SubprocessCollect(65536))),
            5000));
        var outcome = await handle.Done;
        Assert.Equal(0, outcome.ExitCode, "the child exits 0");
        Assert.Equal("got:hello", ReadAll(handle.Collected.Stdout).TrimEnd(), "the batch stdin reaches the child");
    }

    public static async Task WaitForExit_HonorsCancellation()
    {
        var service = Boot(out var ctx);
        using var _ = ctx;
        var handle = service.Spawn(Cmd("ping -n 60 127.0.0.1 >nul"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var exited = await handle.WaitForExitAsync(cts.Token);
        Assert.False(exited, "a cancelled wait reports false");
        handle.Terminate();
        await handle.Done.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
