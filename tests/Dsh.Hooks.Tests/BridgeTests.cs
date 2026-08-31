using System.Text.Json;
using Dsh.Hooks;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Hooks.Tests;

/// <summary>The two bridges over a real loop harness: hook runs, payloads, decisions, context, and the log pairs.</summary>
public static class BridgeTests
{
    public static void ClaudeBridge_RunsPreToolUseHooks_LogsThePair_AndCapturesThePayload()
    {
        using var temp = new TempDir();
        var capture = Path.Combine(temp.Path, "payload.json");
        var hook = HookScripts.WriteCaptureEcho(temp.Path, capture,
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"allow\"}}");
        using var harness = Harness.Create();
        using var bridge = new ClaudeCodeBridge(harness.Ctx,
            new ClaudeCodeBridgeConfig(HookScripts.WriteClaudePreTool(temp.Path, hook, "todo_write")));
        var session = harness.RunTurn("session-cc-pre", "plan the hooks");
        Assert.True(session.Events.OfType<HookInvokedEvent>().Any(evt =>
            evt.Point == "PreToolUse" && evt.Dialect == HookDialect.ClaudeCode && evt.Matcher == "todo_write"),
            "the invoked pair half is logged with the matcher");
        Assert.True(session.Events.OfType<HookResultEvent>().Any(evt => evt.Point == "PreToolUse" && evt.Decision == "allow"),
            "the result pair half is logged with the decoded decision");
        var payload = JsonDocument.Parse(File.ReadAllText(capture)).RootElement;
        Assert.Equal("PreToolUse", payload.GetProperty("hook_event_name").GetString(), "the payload names the event");
        Assert.Equal("todo_write", payload.GetProperty("tool_name").GetString(), "the payload names the tool");
        Assert.Equal(MockSpike.ToolCallId, payload.GetProperty("tool_use_id").GetString(), "the payload carries the call id");
        Assert.True(harness.Llm.Requests.Count >= 2, "the mock turn ran its two calls");
    }

    public static void ClaudeBridge_DenyBlocksTheTool()
    {
        using var temp = new TempDir();
        var hook = HookScripts.WriteCaptureEcho(temp.Path, Path.Combine(temp.Path, "p.json"),
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"no hooks here\"}}");
        using var harness = Harness.Create();
        using var bridge = new ClaudeCodeBridge(harness.Ctx,
            new ClaudeCodeBridgeConfig(HookScripts.WriteClaudePreTool(temp.Path, hook, "todo_write")));
        var session = harness.RunTurn("session-cc-deny", "plan");
        Assert.True(session.Events.OfType<HookResultEvent>().Any(evt => evt.Decision == "deny"),
            "the deny decision is recorded");
        Assert.True(session.Events.OfType<ToolResultEvent>().Any(evt =>
            evt.Message.Result.Content.OfType<TextBlock>().Any(block => block.Text.Contains("no hooks here", StringComparison.Ordinal))),
            "the denied tool materializes the hook's reason as its error");
    }

    public static void ClaudeBridge_UserPromptContext_JoinsTheRequest()
    {
        using var temp = new TempDir();
        var hook = HookScripts.WriteCaptureEcho(temp.Path, Path.Combine(temp.Path, "p.json"),
            "{\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":\"plan carefully\"}}");
        using var harness = Harness.Create();
        using var bridge = new ClaudeCodeBridge(harness.Ctx,
            new ClaudeCodeBridgeConfig(HookScripts.WriteClaudePreTool(temp.Path, hook, "*", point: "UserPromptSubmit")));
        _ = harness.RunTurn("session-cc-ctx", "go");
        Assert.True(harness.Llm.Requests.Count > 0 && harness.Llm.Requests[0].Messages
                .SelectMany(message => message.Content).OfType<TextBlock>()
                .Any(block => block.Text.Contains("plan carefully", StringComparison.Ordinal)),
            "the hook's additionalContext joins the first model request");
    }

    public static void ClaudeBridge_SessionStart_InjectsContext()
    {
        using var temp = new TempDir();
        var capture = Path.Combine(temp.Path, "s.json");
        var hook = HookScripts.WriteCaptureEcho(temp.Path, capture,
            "{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"session begins\"}}");
        using var harness = Harness.Create();
        using var bridge = new ClaudeCodeBridge(harness.Ctx,
            new ClaudeCodeBridgeConfig(HookScripts.WriteClaudePreTool(temp.Path, hook, "*", point: "SessionStart")));
        var handle = harness.Loop.Create(new SessionId("session-cc-start"), new Dsh.Agent.AgentOptions
        {
            Provider = Dsh.Spike.MockLlmProvider.Provider,
            Model = Dsh.Spike.MockLlmProvider.Model,
        });
        // The detached hook runs beside the create; wait for its capture, then a short settle for
        // the inject continuation, before the first prompt observes the context.
        Assert.WaitUntil(() => File.Exists(capture), 15000);
        Thread.Sleep(500);
        var driver = harness.Loop.GetLoop(new SessionId("session-cc-start"))!;
        driver.Followup(new UserMessage
        {
            Id = new MessageId(Guid.NewGuid().ToString("N")),
            Content = new ContentBlock[] { new TextBlock("go") },
            Source = new UserSource(),
        });
        driver.WhenIdleAsync().GetAwaiter().GetResult();
        Assert.True(harness.Llm.Requests.Count > 0 && harness.Llm.Requests[0].Messages
                .SelectMany(message => message.Content).OfType<TextBlock>()
                .Any(block => block.Text.Contains("session begins", StringComparison.Ordinal)),
            "the SessionStart context injects before the first request");
    }

    public static void ClaudeBridge_StopHook_IsObserved()
    {
        using var temp = new TempDir();
        var hook = HookScripts.WriteCaptureEcho(temp.Path, Path.Combine(temp.Path, "s.json"),
            "{\"hookSpecificOutput\":{\"hookEventName\":\"Stop\",\"permissionDecision\":\"allow\"}}");
        using var harness = Harness.Create();
        using var bridge = new ClaudeCodeBridge(harness.Ctx,
            new ClaudeCodeBridgeConfig(HookScripts.WriteClaudePreTool(temp.Path, hook, "*", point: "Stop")));
        var session = harness.RunTurn("session-cc-stop", "plan");
        Assert.True(session.Events.OfType<HookInvokedEvent>().Any(evt => evt.Point == "Stop"),
            "the Stop hook is invoked at the stopping boundary");
        Assert.Equal(2, harness.Llm.Requests.Count, "a non-blocking Stop hook lets the turn stop");
    }

    public static void CodexBridge_RunsPreToolUseHooks_WithCodexPayloads()
    {
        using var temp = new TempDir();
        var capture = Path.Combine(temp.Path, "codex.json");
        var hook = HookScripts.WriteCaptureEcho(temp.Path, capture,
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"codex says no\"}}");
        using var harness = Harness.Create();
        using var bridge = new CodexBridge(harness.Ctx,
            new CodexBridgeConfig(HookScripts.WriteCodexConfig(temp.Path, "PreToolUse", hook, "todo_write"), Model: "codex-model"));
        var session = harness.RunTurn("session-codex", "plan");
        Assert.True(session.Events.OfType<HookResultEvent>().Any(evt => evt.Point == "PreToolUse" && evt.Decision == "deny"),
            "the codex deny decision is recorded");
        var payload = JsonDocument.Parse(File.ReadAllText(capture)).RootElement;
        Assert.Equal("PreToolUse", payload.GetProperty("hook_event_name").GetString(), "the payload names the event");
        Assert.Equal("todo_write", payload.GetProperty("tool_name").GetString(), "the payload names the tool");
        Assert.Equal("codex-model", payload.GetProperty("model").GetString(), "the model rides along");
        Assert.Equal("default", payload.GetProperty("permission_mode").GetString(), "the permission mode rides along");
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("turn_id").ValueKind, "the turn id rides along");
        Assert.Equal("", payload.GetProperty("tool_input").GetProperty("command").GetString(), "the codex command shape is empty for todo_write");
        Assert.True(session.Events.OfType<ToolResultEvent>().Any(evt =>
            evt.Message.Result.Content.OfType<TextBlock>().Any(block => block.Text.Contains("codex says no", StringComparison.Ordinal))),
            "the denied tool materializes the codex reason");
    }

    public static void CodexBridge_PlainStdout_BecomesContext()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Path, "plain.cmd");
        File.WriteAllText(hook, "@echo off\r\necho plain context text\r\n");
        using var harness = Harness.Create();
        using var bridge = new CodexBridge(harness.Ctx,
            new CodexBridgeConfig(HookScripts.WriteCodexConfig(temp.Path, "UserPromptSubmit", hook, "*")));
        _ = harness.RunTurn("session-codex-ctx", "go");
        Assert.True(harness.Llm.Requests.Count > 0 && harness.Llm.Requests[0].Messages
                .SelectMany(message => message.Content).OfType<TextBlock>()
                .Any(block => block.Text.Contains("plain context text", StringComparison.Ordinal)),
            "clean plain stdout becomes injected context on UserPromptSubmit");
    }
}

/// <summary>The mock's fixture-fixed tool-call id (aliased for the bridge tests).</summary>
internal static class MockSpike
{
    public const string ToolCallId = Dsh.Spike.MockLlmProvider.ToolCallIdValue;
}
