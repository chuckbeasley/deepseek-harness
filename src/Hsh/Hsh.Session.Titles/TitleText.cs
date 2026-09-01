using System.Text;

namespace Harness.Session.Titles;

/// <summary>Title text normalization and UTF-8-safe truncation (port of the TS title normalize module).</summary>
public static class TitleText
{
    /// <summary>
    /// Derive the deterministic first-prompt fallback: controls stripped, whitespace normalized,
    /// the leading <paramref name="maxWords"/> words, truncated to a UTF-8 byte budget without
    /// splitting a code point.
    /// </summary>
    public static string FallbackTitle(string input, int maxWords, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        var words = CleanTitleText(input).Split(' ').Where(word => word.Length > 0).Take(maxWords);
        return TruncateTitleUtf8(string.Join(' ', words), maxBytes).TrimEnd();
    }

    /// <summary>Remove escape sequences and controls, then produce one trimmed, whitespace-normalized line.</summary>
    public static string CleanTitleText(string input)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(input, @"(?:\u001B\]|\u009D)(?:(?!\u0007|\u001B\\)[\s\S])*(?:\u0007|\u001B\\|$)", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?:\u001B\[|\u009B)[0-?]*[ -/]*[@-~]", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\u001B[@-_]", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\u200B\u200E\u200F\u202A-\u202E\u2060-\u2064\u2066-\u206F\uFEFF]", "");
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    /// <summary>Truncate a string to a UTF-8 byte budget without splitting a Unicode code point.</summary>
    public static string TruncateTitleUtf8(string input, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(input);
        if (bytes <= maxBytes) return input;
        var used = 0;
        var output = new StringBuilder();
        foreach (var character in input)
        {
            var count = Encoding.UTF8.GetByteCount(character.ToString());
            if (used + count > maxBytes) break;
            output.Append(character);
            used += count;
        }
        return output.ToString();
    }
}