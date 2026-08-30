using System.Text.Json;
using Dsh.Hooks;
using Dsh.Session;

namespace Dsh.Hooks.Tests;

/// <summary>The matcher semantics, the outcome codec, and the log-only event records.</summary>
public static class HookTests
{
    public static void Matcher_MatchAllSentinels()
    {
        Assert.True(HookMatcher.Matches(null, "anything", MatcherMode.ClaudeCode), "absent matches all");
        Assert.True(HookMatcher.Matches("", "anything", MatcherMode.Codex), "empty matches all");
        Assert.True(HookMatcher.Matches("*", "anything", MatcherMode.Codex), "star matches all");
        Assert.True(HookMatcher.MatcherDiagnostic("*", MatcherMode.ClaudeCode) is null, "match-all sentinels are valid");
    }

    public static void Matcher_ClaudeLiteralAlternation()
    {
        Assert.True(HookMatcher.Matches("tool-a|tool_b", "tool-a", MatcherMode.ClaudeCode), "literal alternatives exact-match");
        Assert.False(HookMatcher.Matches("tool-a|tool_b", "tool-c", MatcherMode.ClaudeCode), "a non-member literal does not match");
        Assert.True(HookMatcher.MatcherDiagnostic("tool-a|tool_b", MatcherMode.ClaudeCode) is null, "literal patterns are valid");
    }

    public static void Matcher_RegexAndInvalidRegexes()
    {
        Assert.True(HookMatcher.Matches("^tool-[0-9]+$", "tool-42", MatcherMode.Codex), "codex patterns are unanchored regexes");
        Assert.True(HookMatcher.Matches("tool-[0-9]", "xtool-7y", MatcherMode.Codex), "unanchored regex matches inside the query");
        Assert.False(HookMatcher.Matches("([unclosed", "anything", MatcherMode.Codex), "an invalid regex is a non-match");
        Assert.True(HookMatcher.MatcherDiagnostic("([unclosed", MatcherMode.Codex) is not null, "the diagnostic rejects invalid regexes");
        Assert.True(HookMatcher.MatcherDiagnostic("tool-a|tool_b", MatcherMode.Codex) is null, "codex accepts the literal-looking pattern as regex");
    }

    public static void Codec_BlockingExit()
    {
        var output = HookCodec.ParseHookOutput(2, "", "the model must not proceed");
        Assert.Equal("block", output.Decision, "exit 2 blocks");
        Assert.Equal("the model must not proceed", output.Reason, "stderr becomes the block reason");
        Assert.Equal("", output.Stdout, "stdout is preserved verbatim");
    }

    public static void Codec_StructuredOutputOnExitZero()
    {
        var output = HookCodec.ParseHookOutput(0, "{\"continue\":false,\"stopReason\":\"hold on\"}", "");
        Assert.Equal(false, output.Continue, "continue:false folds");
        Assert.Equal("hold on", output.StopReason, "stopReason folds");
        Assert.Null(output.Decision, "no decision channel was exercised");
    }

    public static void Codec_PermissionDecisionOverridesTopLevel()
    {
        var output = HookCodec.ParseHookOutput(
            0,
            "{\"decision\":\"approve\",\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"no\"}}",
            "",
            "PreToolUse");
        Assert.Equal("deny", output.Decision, "permissionDecision overrides the top-level decision");
        Assert.Equal("no", output.Reason, "permissionDecisionReason folds");
        Assert.Equal("PreToolUse", output.HookEventName, "the claimed discriminator is recorded");
    }

    public static void Codec_MismatchedEventDiscardsEventScopedFields()
    {
        var output = HookCodec.ParseHookOutput(
            0,
            "{\"continue\":false,\"hookSpecificOutput\":{\"hookEventName\":\"Other\",\"permissionDecision\":\"allow\"}}",
            "",
            "PreToolUse");
        Assert.Null(output.Decision, "a mismatched discriminator cannot affect the firing event");
        Assert.Equal("Other", output.HookEventName, "the claimed discriminator is still recorded");
        Assert.Equal(false, output.Continue, "top-level fields survive the mismatch");
    }

    public static void Codec_MalformedAndPlainStdout()
    {
        var output = HookCodec.ParseHookOutput(0, "{not json", "");
        Assert.Equal("{not json", output.Stdout, "malformed JSON stays plain stdout");
        Assert.Null(output.Decision, "no structured decision folds");
        var plain = HookCodec.ParseHookOutput(0, "just text", "");
        Assert.Equal("just text", plain.Stdout, "plain stdout is preserved");
    }

    public static void Codec_OutOfBandDenyIsIgnored()
    {
        var output = HookCodec.ParseHookOutput(0, "{\"decision\":\"deny\"}", "");
        Assert.Null(output.Decision, "an out-of-band top-level deny is invalid and ignored");
    }

    public static void Events_RoundTripTheJsonlSerializer()
    {
        HookEvents.RegisterEventTypes();
        var invoked = new HookInvokedEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            Turn = 3,
            Point = "PreToolUse",
            Dialect = HookDialect.ClaudeCode,
            HandlerId = "hook-1",
        };
        var result = new HookResultEvent
        {
            Id = "evt-1",
            Seq = 1,
            TimeMs = 2,
            Turn = 3,
            Point = "PreToolUse",
            HandlerId = "hook-1",
            Decision = "allow",
            DurationMs = 5,
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var invokedBack = JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize<SessionEvent>(invoked, options), options);
        var resultBack = JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize<SessionEvent>(result, options), options);
        Assert.True(invokedBack is HookInvokedEvent { Dialect: HookDialect.ClaudeCode }, "hook/invoked round-trips");
        Assert.True(resultBack is HookResultEvent { Decision: "allow" }, "hook/result round-trips");
    }
}
