using System.Text.Json;
using Harness.Llm;
using Harness.Shell;
using Harness.Tools;

namespace Harness.Shell.Tests;

/// <summary>The executor's resolve/run semantics and the bash tool's model-facing surface.</summary>
public static class ShellTests
{
    public static async Task Run_EchoesStdoutWithExitZero()
    {
        using var harness = new ShellHarness();
        var result = harness.Shell.Run(harness.Shell.Resolve(new ShellExecRequest("echo hello")));
        Assert.Equal(0, result.ExitCode, "the command exits 0");
        Assert.Equal("hello", result.Stdout.Text.TrimEnd(), "stdout is collected");
        Assert.False(result.TimedOut, "the run did not time out");
        Assert.False(result.Aborted, "the run was not aborted");
    }

    public static async Task Resolve_FillsAndCapsDefaults()
    {
        using var harness = new ShellHarness();
        var spec = harness.Shell.Resolve(new ShellExecRequest("echo hi", TimeoutMs: 1_000_000));
        Assert.Equal(1_000_000, spec.TimeoutMs, "an explicit timeout carries through");
        Assert.Equal(256 * 1024, spec.StdoutMaxBytes, "the default stdout cap applies");
        var relative = harness.Shell.Resolve(new ShellExecRequest("echo hi", Workdir: "sub"));
        Assert.True(Path.IsPathRooted(relative.Workdir), "a relative workdir resolves against the default");
        Assert.True(relative.Workdir.EndsWith("sub", StringComparison.OrdinalIgnoreCase), "the relative segment joins the default workdir");
    }

    public static async Task Run_WorkdirOverrideApplies()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hsh-shell-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var harness = new ShellHarness();
            var result = harness.Shell.Run(harness.Shell.Resolve(new ShellExecRequest("cd", Workdir: temp)));
            Assert.Equal(0, result.ExitCode, "the command exits 0");
            Assert.True(result.Stdout.Text.Contains(temp, StringComparison.OrdinalIgnoreCase), "the child runs in the requested workdir");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    public static async Task Run_TimeoutKillsAndClassifies()
    {
        using var harness = new ShellHarness();
        var result = harness.Shell.Run(harness.Shell.Resolve(new ShellExecRequest("ping -n 60 127.0.0.1 >nul", TimeoutMs: 400)));
        Assert.True(result.TimedOut, "the executor's timeout owns the classification");
        Assert.False(result.Aborted, "an abort did not race the timeout");
    }

    public static async Task Run_CallerCancellationClassifiesAborted()
    {
        using var harness = new ShellHarness();
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => harness.Shell.Run(harness.Shell.Resolve(new ShellExecRequest("ping -n 60 127.0.0.1 >nul", CancellationToken: cts.Token))));
        await Task.Delay(400);
        cts.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Aborted, "the caller's cancellation owns the classification");
        Assert.False(result.TimedOut, "the timeout did not race the abort");
    }

    public static async Task BashTool_ExecutesThroughTheToolRuntime()
    {
        using var harness = new ShellHarness();
        var input = new ToolExecutionInput(
            new ToolCallId("call-shell-1"),
            "bash",
            JsonSerializer.SerializeToElement(new { command = "echo hi", description = "Say hi" }),
            CancellationToken.None);
        var result = await harness.Tools.ExecuteAsync(input, CancellationToken.None);
        Assert.False(result.IsError, "a zero-exit command is a success result");
        var text = result.Content.OfType<TextBlock>().Single().Text;
        Assert.True(text.Contains("hi"), "the rendered result carries the stdout");
        Assert.False(text.Contains("[exit code"), "a zero exit renders no exit marker");
    }

    public static async Task BashTool_RendersNonZeroExitMarker()
    {
        using var harness = new ShellHarness();
        var input = new ToolExecutionInput(
            new ToolCallId("call-shell-2"),
            "bash",
            JsonSerializer.SerializeToElement(new { command = "exit 3", description = "Fail deliberately" }),
            CancellationToken.None);
        var result = await harness.Tools.ExecuteAsync(input, CancellationToken.None);
        Assert.False(result.IsError, "a non-zero exit resolves as a result, not a failure");
        var text = result.Content.OfType<TextBlock>().Single().Text;
        Assert.True(text.Contains("[exit code: 3]"), "the rendered result carries the exit marker");
    }

    public static async Task BashTool_RejectsInvalidArguments()
    {
        using var harness = new ShellHarness();
        var input = new ToolExecutionInput(
            new ToolCallId("call-shell-3"),
            "bash",
            JsonSerializer.SerializeToElement(new { command = "   ", description = "  " }),
            CancellationToken.None);
        var result = await harness.Tools.ExecuteAsync(input, CancellationToken.None);
        Assert.True(result.IsError, "an empty command is an error result");
        Assert.True(result.Content.OfType<TextBlock>().Single().Text.Contains("invalid command"), "the error names the invalid field");
    }

    public static async Task Run_MissingShellFailsLoud()
    {
        using var harness = new ShellHarness("definitely-not-a-shell.exe");
        try
        {
            harness.Shell.Run(harness.Shell.Resolve(new ShellExecRequest("echo hi")));
            Assert.True(false, "a missing shell executable must fail loud");
        }
        catch (InvalidOperationException error)
        {
            Assert.True(error.Message.Contains("definitely-not-a-shell.exe"), "the error names the missing shell");
        }
    }
}
