using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Shell;
using Dsh.Tools;

namespace Dsh.Sandbox.Tests;

/// <summary>
/// The additive shell wiring: the bash tool result JSON carries the sandbox facts and the render
/// path reconstructs them, so a denied run renders the shared denial marker.
/// </summary>
public static class ShellToolRoundTripTests
{
    public static void SandboxFactsRoundTripThroughTheShellToolResult()
    {
        using var harness = Harness.Create();
        var definition = ShellTools.Definition(harness.Ctx);
        var value = new JsonObject
        {
            ["kind"] = "foreground",
            ["exitCode"] = 1,
            ["signal"] = null,
            ["timedOut"] = false,
            ["aborted"] = false,
            ["timeoutMs"] = 1000,
            ["stdout"] = new JsonObject { ["text"] = "touched a protected file", ["truncated"] = false },
            ["stderr"] = new JsonObject { ["text"] = string.Empty, ["truncated"] = false },
            ["sandbox"] = JsonSerializer.SerializeToNode(new ShellSandboxInfo(SandboxMode.ReadOnly, Denied: true)),
        };
        var rendered = definition.Render!(JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(value));
        var text = Assert.Single(rendered) as TextBlock;
        Assert.NotNull(text, "the render emits one text block");
        Assert.True(text!.Text.Contains("[sandbox: file access denied under read-only mode]", StringComparison.Ordinal), "a denied run renders the denial marker");
        Assert.True(text.Text.Contains("[exit code: 1]", StringComparison.Ordinal), "the exit marker still renders");
    }

    public static void AnUnsandboxedResultRendersNoSandboxMarker()
    {
        using var harness = Harness.Create();
        var definition = ShellTools.Definition(harness.Ctx);
        var value = new JsonObject
        {
            ["kind"] = "foreground",
            ["exitCode"] = 0,
            ["signal"] = null,
            ["timedOut"] = false,
            ["aborted"] = false,
            ["timeoutMs"] = 1000,
            ["stdout"] = new JsonObject { ["text"] = "hello", ["truncated"] = false },
            ["stderr"] = new JsonObject { ["text"] = string.Empty, ["truncated"] = false },
        };
        var rendered = definition.Render!(JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(value));
        var text = Assert.Single(rendered) as TextBlock;
        Assert.NotNull(text, "the render emits one text block");
        Assert.True(text!.Text.Contains("hello", StringComparison.Ordinal), "the output body renders");
        Assert.False(text.Text.Contains("file access denied", StringComparison.Ordinal), "an unsandboxed result has no denial marker");
    }
}