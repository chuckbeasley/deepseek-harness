using System.Text.Json;
using Dsh.Hooks;

namespace Dsh.Hooks.Tests;

/// <summary>Merge, config-parser, and runner tests (ports of the TS merge/config/runner specs).</summary>
public static class HookExtensionsTests
{
    public static void Merge_FoldsTheMostRestrictiveDecision()
    {
        var merged = HookMerge.MergeHookOutputs(new[]
        {
            Output("allow"),
            Output(null),
            Output("deny", "no"),
            Output("ask", "maybe"),
        });
        Assert.Equal("deny", merged.Decision, "deny outranks ask and allow");
        Assert.Equal("no", merged.Reason, "only the winning rank's reasons surface");
        Assert.False(merged.Stop, "no hook asked to halt");

        var allowed = HookMerge.MergeHookOutputs(new[] { Output("allow"), Output("approve") });
        Assert.Equal("allow", allowed.Decision, "allow and approve both fold to allow");
        Assert.Equal("none", HookMerge.MergeHookOutputs(Array.Empty<HookOutput>()).Decision, "no hooks yield a neutral decision");
    }

    public static void Merge_AccumulatesContextAndStop()
    {
        var merged = HookMerge.MergeHookOutputs(new[]
        {
            Output("allow", AdditionalContext: "one"),
            Output(null, StopReason: "halt now", Continue: false),
            Output(null, StopReason: "later", Continue: false),
            Output(null, SystemMessage: "heads up"),
            Output("allow", AdditionalContext: "two"),
        });
        Assert.True(merged.Stop, "the first continue:false is sticky");
        Assert.Equal("halt now", merged.StopReason, "the first halting hook's reason wins");
        Assert.True(merged.AdditionalContext.SequenceEqual(new[] { "one", "two" }), "context accumulates in hook order");
        Assert.True(merged.SystemMessages.SequenceEqual(new[] { "heads up" }), "system messages accumulate");
    }

    public static void ClaudeConfig_ParsesEventsMatchersAndSubstitutions()
    {
        var parsed = ClaudeCodeConfig.Parse(JsonSerializer.Serialize(new
        {
            hooks = new Dictionary<string, object>
            {
                ["PreToolUse"] = new object[]
                {
                    new { matcher = "todo_write|plan", hooks = new object[]
                    {
                        new { type = "command", command = "run ${CLAUDE_PLUGIN_ROOT}/x ${CLAUDE_PROJECT_DIR}" },
                        new { type = "bash", command = "nope" },
                    } },
                },
                ["UserPromptSubmit"] = new object[]
                {
                    new { matcher = "ignored", hooks = new object[] { new { command = "prompt-hook", timeout = 7 } } },
                },
            },
        }), pluginRoot: "C:\\plug", projectDir: "C:\\proj");
        var preTool = parsed.Config["PreToolUse"];
        Assert.Equal(1, preTool.Count, "one matcher group");
        Assert.Equal("run C:\\plug/x C:\\proj", preTool[0].Hooks[0].Command, "substitutions apply to the command");
        Assert.Equal(1, parsed.Skipped.Count, "the non-command hook is skipped");
        Assert.Equal("bash", parsed.Skipped[0].Type, "the skip names the type");
        var prompt = parsed.Config["UserPromptSubmit"][0];
        Assert.Null(prompt.Matcher, "UserPromptSubmit discards its matcher");
        Assert.Equal(7, prompt.Hooks[0].TimeoutSec, "the per-hook timeout rides along");
    }

    public static void ClaudeConfig_RejectsInvalidMatchers()
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(() => ClaudeCodeConfig.Parse(
            JsonSerializer.Serialize(new
            {
                hooks = new Dictionary<string, object>
                {
                    ["PreToolUse"] = new object[] { new { matcher = "[", hooks = new object[] { new { command = "x" } } } },
                },
            })), "an invalid matcher rejects the complete config");
        Assert.Contains("invalid claude-code regex matcher", error.Message, "the failure names the dialect and pattern");
        var empty = ClaudeCodeConfig.Parse("{}");
        Assert.Equal(0, empty.Config.Count, "an empty config parses to nothing");
    }

    public static void CodexConfig_ParsesAndSkips()
    {
        var parsed = CodexConfig.Parse(JsonSerializer.Serialize(new
        {
            hooks = new Dictionary<string, object>
            {
                ["PreToolUse"] = new object[]
                {
                    new { matcher = ".*", hooks = new object[]
                    {
                        new { type = "command", command = "codex-hook", timeout = 3 },
                        new { type = "command", command = "async-one", async = true },
                        new { type = "bash", command = "nope" },
                    } },
                },
                ["Stop"] = new object[] { new { matcher = "ignored", hooks = new object[] { new { command = "stop-hook", timeoutSec = 9 } } } },
            },
        }));
        var preTool = parsed.Config["PreToolUse"][0];
        Assert.Equal(1, preTool.Hooks.Count, "only the sync command hook survives");
        Assert.Equal(3, preTool.Hooks[0].TimeoutSec, "the timeout alias rides along");
        Assert.Equal(2, parsed.Skipped.Count, "async and non-command hooks are skipped");
        Assert.Null(parsed.Config["Stop"][0].Matcher, "Stop discards its matcher");
        Assert.Equal(9, parsed.Config["Stop"][0].Hooks[0].TimeoutSec, "the timeoutSec alias rides along");
    }

    public static void Runner_RunsRealHooks_AndContainsInfrastructureFailure()
    {
        using var temp = new TempDir();
        using var harness = Harness.Create();
        var shell = harness.Ctx.Get<Dsh.Shell.IShellService>("shell")!;
        var capture = Path.Combine(temp.Path, "payload.txt");
        var hook = HookScripts.WriteCaptureEcho(temp.Path, capture, "{\"continue\":false,\"stopReason\":\"stop it\"}");
        var result = HookRunner.RunHook(shell, new CommandHook(hook), new HookRunner.RunHookOptions(
            new { hello = "world" }, Env: null, Cwd: null, CancellationToken.None,
            TrailingNewline: true, HookRunner.DefaultHookTimeoutMs, null));
        Assert.Equal(0, result.Output.ExitCode, "the hook exits clean");
        Assert.Equal(false, result.Output.Continue, "the structured continue:false is decoded");
        Assert.Equal("stop it", result.Output.StopReason, "the stop reason is decoded");
        var captured = File.ReadAllText(capture);
        Assert.True(captured.Contains("\"hello\":\"world\"", StringComparison.Ordinal), "the payload reaches the hook's stdin");
        Assert.True(captured.EndsWith("\n", StringComparison.Ordinal), "the claude framing appends a trailing newline");

        var blocked = HookRunner.RunHook(shell, new CommandHook(HookScripts.WriteBlockingEcho(temp.Path, "denied")),
            new HookRunner.RunHookOptions(new { }, Env: null, Cwd: null, CancellationToken.None,
                TrailingNewline: false, HookRunner.DefaultHookTimeoutMs, null));
        Assert.Equal(2, blocked.Output.ExitCode, "the blocking exit code survives");
        Assert.Equal("block", blocked.Output.Decision, "exit 2 decodes as a block");

        var missing = HookRunner.RunHook(shell, new CommandHook("C:\\no\\such\\hook.cmd"),
            new HookRunner.RunHookOptions(new { }, Env: null, Cwd: Path.Combine(temp.Path, "no-such-dir"),
                CancellationToken.None, TrailingNewline: false, HookRunner.DefaultHookTimeoutMs, null));
        Assert.Null(missing.Output.ExitCode, "an infrastructure failure carries no exit code");
        Assert.True(missing.Output.Stderr.Length > 0, "the failure lands on stderr for the record");
    }

    private static HookOutput Output(string? decision, string? Reason = null, string? AdditionalContext = null,
        string? StopReason = null, bool? Continue = null, string? SystemMessage = null)
        => new()
        {
            Decision = decision,
            Reason = Reason,
            AdditionalContext = AdditionalContext,
            StopReason = StopReason,
            Continue = Continue,
            SystemMessage = SystemMessage,
        };
}
