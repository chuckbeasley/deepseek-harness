namespace Dsh.Hooks.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("matcher: match-all sentinels", HookTests.Matcher_MatchAllSentinels),
        ("matcher: claude literal alternation", HookTests.Matcher_ClaudeLiteralAlternation),
        ("matcher: regex and invalid regexes", HookTests.Matcher_RegexAndInvalidRegexes),
        ("codec: blocking exit", HookTests.Codec_BlockingExit),
        ("codec: structured output on exit zero", HookTests.Codec_StructuredOutputOnExitZero),
        ("codec: permissionDecision overrides top-level", HookTests.Codec_PermissionDecisionOverridesTopLevel),
        ("codec: mismatched event discards event-scoped fields", HookTests.Codec_MismatchedEventDiscardsEventScopedFields),
        ("codec: malformed and plain stdout", HookTests.Codec_MalformedAndPlainStdout),
        ("codec: out-of-band deny ignored", HookTests.Codec_OutOfBandDenyIsIgnored),
        ("events round-trip the JSONL serializer", HookTests.Events_RoundTripTheJsonlSerializer),
        ("merge: the folded decision precedence", HookExtensionsTests.Merge_FoldsTheMostRestrictiveDecision),
        ("merge: stop and context accumulation", HookExtensionsTests.Merge_AccumulatesContextAndStop),
        ("config: claude parses events, matchers, and substitutions", HookExtensionsTests.ClaudeConfig_ParsesEventsMatchersAndSubstitutions),
        ("config: claude rejects invalid matchers", HookExtensionsTests.ClaudeConfig_RejectsInvalidMatchers),
        ("config: codex parses and skips", HookExtensionsTests.CodexConfig_ParsesAndSkips),
        ("runner: real hooks decode and infrastructure failures contain", HookExtensionsTests.Runner_RunsRealHooks_AndContainsInfrastructureFailure),
        ("bridge: claude PreToolUse runs, logs the pair, and captures the payload", BridgeTests.ClaudeBridge_RunsPreToolUseHooks_LogsThePair_AndCapturesThePayload),
        ("bridge: claude deny blocks the tool", BridgeTests.ClaudeBridge_DenyBlocksTheTool),
        ("bridge: claude UserPromptSubmit context joins the request", BridgeTests.ClaudeBridge_UserPromptContext_JoinsTheRequest),
        ("bridge: claude SessionStart injects context", BridgeTests.ClaudeBridge_SessionStart_InjectsContext),
        ("bridge: claude Stop hook is observed", BridgeTests.ClaudeBridge_StopHook_IsObserved),
        ("bridge: codex PreToolUse denies with the codex payload", BridgeTests.CodexBridge_RunsPreToolUseHooks_WithCodexPayloads),
        ("bridge: codex plain stdout becomes context", BridgeTests.CodexBridge_PlainStdout_BecomesContext),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                var watchdog = Task.Run(run);
                if (!watchdog.Wait(TimeSpan.FromSeconds(90)))
                {
                    throw new AssertionException("TIMEOUT (test hung)");
                }
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL  {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}