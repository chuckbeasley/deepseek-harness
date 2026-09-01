using System.Text.RegularExpressions;

namespace Harness.Hooks;

/// <summary>
/// Matcher shared by both hook dialects (port of matcher.ts). Claude treats word-and-pipe
/// patterns as literal alternatives and other patterns as regex; Codex treats every non-empty
/// pattern as an unanchored regex. Missing, empty, and "*" match all. Runtime matching contains
/// invalid regexes as non-matches; config parsers reject them via <see cref="MatcherDiagnostic"/>.
/// </summary>
public static class HookMatcher
{
    private static readonly Regex ClaudeLiteral = new("^[A-Za-z0-9_|]+$", RegexOptions.Compiled);

    /// <summary>Validate one matcher; returns null for a valid matcher, otherwise a stable diagnostic.</summary>
    public static string? MatcherDiagnostic(string? matcher, MatcherMode mode)
    {
        if (IsMatchAll(matcher)) return null;
        if (mode == MatcherMode.ClaudeCode && ClaudeLiteral.IsMatch(matcher)) return null;
        return CompileRegex(matcher) is null
            ? $"invalid {ModeName(mode)} regex matcher {JsonQuote(matcher)}"
            : null;
    }

    /// <summary>Whether the pattern selects the query under the given dialect; invalid regexes are non-matches.</summary>
    public static bool Matches(string? matcher, string query, MatcherMode mode)
    {
        if (IsMatchAll(matcher)) return true;
        if (mode == MatcherMode.ClaudeCode && ClaudeLiteral.IsMatch(matcher))
        {
            return matcher.Split('|').Contains(query, StringComparer.Ordinal);
        }
        return CompileRegex(matcher)?.IsMatch(query) ?? false;
    }

    private static bool IsMatchAll(string? matcher)
        => matcher is null || matcher.Length == 0 || matcher == "*";

    private static Regex? CompileRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            // Regex construction is the try's only operation; malformed pattern syntax is the
            // only expected failure.
            return null;
        }
    }

    private static string ModeName(MatcherMode mode)
        => mode == MatcherMode.ClaudeCode ? "claude-code" : "codex";

    private static string JsonQuote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
