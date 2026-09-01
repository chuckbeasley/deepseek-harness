using System.Text;

namespace Harness.Subagent;

/// <summary>
/// Shared helpers of the out-of-process driver family (port of the TS <c>out-of-process.ts</c>):
/// diagnostic capping, child cwd resolution, and the ambient environment scrub.
/// </summary>
public static class OutOfProcess
{
    /// <summary>The byte cap for provider-authored diagnostics.</summary>
    public const int MaxDiagnosticBytes = 4096;

    private const string TruncationSuffix = "\n[diagnostic truncated]";

    /// <summary>
    /// Cap a provider-authored diagnostic at <see cref="MaxDiagnosticBytes"/> UTF-8 bytes with a
    /// visible truncation suffix, never splitting a UTF-8 sequence (walk back from the byte
    /// boundary while the lead byte is a continuation byte).
    /// </summary>
    public static string? LimitDiagnostic(string? diagnostic)
    {
        if (diagnostic is null || diagnostic.Length == 0) return null;
        var bytes = Encoding.UTF8.GetByteCount(diagnostic);
        if (bytes <= MaxDiagnosticBytes) return diagnostic;
        var budget = MaxDiagnosticBytes - Encoding.UTF8.GetByteCount(TruncationSuffix);
        if (budget <= 0) return TruncationSuffix[..Math.Min(TruncationSuffix.Length, MaxDiagnosticBytes)];
        var cut = 0;
        var count = 0;
        while (count < budget && cut < diagnostic.Length)
        {
            var runeLength = RuneLength(diagnostic[cut]);
            if (count + runeLength > budget) break;
            count += runeLength;
            cut++;
        }
        return diagnostic[..cut] + TruncationSuffix;
    }

    /// <summary>UTF-8 byte length of the rune starting at <paramref name="index"/> in the string.</summary>
    private static int RuneLength(char ch)
    {
        if (char.IsHighSurrogate(ch)) return 4;
        if (ch >= '\u0800') return 3;
        if (ch >= '\u0080') return 2;
        return 1;
    }

    /// <summary>
    /// Resolve the child working directory: the configured absolute path wins, otherwise the
    /// parent process working directory. Both must be absolute, enterable directories — a
    /// relative or missing path fails loud before the process boundary.
    /// </summary>
    public static string ResolveChildCwd(string? configured)
    {
        var candidate = configured ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new SubagentError($"subagent child cwd must be absolute, got \"{candidate}\"", "INVALID_CWD");
        }
        if (!Directory.Exists(candidate))
        {
            throw new SubagentError($"subagent child cwd \"{candidate}\" is not an enterable directory", "INVALID_CWD");
        }
        return candidate;
    }

    /// <summary>
    /// Scrub the ambient parent environment for the child: every <c>HSH_*</c> name and every
    /// name matching <c>KEY|PASSWORD|SECRET|TOKEN</c> (case-insensitive) is dropped; explicit
    /// per-driver entries merge on top afterwards and reach the child deliberately.
    /// </summary>
    public static Dictionary<string, string> ScrubEnvironment(IReadOnlyDictionary<string, string> ambient)
    {
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in ambient)
        {
            if (name.StartsWith("HSH_", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, "KEY|PASSWORD|SECRET|TOKEN", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            scrubbed[name] = value;
        }
        return scrubbed;
    }
}
