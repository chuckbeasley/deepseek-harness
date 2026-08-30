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
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                run();
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
