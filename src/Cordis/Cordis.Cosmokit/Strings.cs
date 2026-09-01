using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Cordis.Cosmokit;

/// <summary>String case, path, and property formatting helpers (port of cosmokit <c>string.ts</c>).</summary>
public static class Strings
{
    /// <summary>Uppercases the first character of a string.</summary>
    public static string Capitalize(string source)
        => source.Length == 0 ? source : char.ToUpperInvariant(source[0]) + source[1..];

    /// <summary>Lowercases the first character of a string.</summary>
    public static string Uncapitalize(string source)
        => source.Length == 0 ? source : char.ToLowerInvariant(source[0]) + source[1..];

    /// <summary>Converts dash or underscore delimited text to camelCase.</summary>
    public static string CamelCase(string source)
        => CamelSeparator.Replace(source, match => char.ToUpperInvariant(match.Value[1]).ToString());

    /// <summary>Converts text to dash-delimited parameter case.</summary>
    public static string ParamCase(string source) => Tokenize(source, 45);

    /// <summary>Converts text to underscore-delimited snake case.</summary>
    public static string SnakeCase(string source) => Tokenize(source, 95);

    /// <summary>Removes one trailing slash from a path string.</summary>
    public static string TrimSlash(string source) => source.EndsWith('/') ? source[..^1] : source;

    /// <summary>Ensures a path starts with <c>/</c> and has no trailing slash.</summary>
    public static string Sanitize(string source) => TrimSlash(source.StartsWith('/') ? source : "/" + source);

    /// <summary>
    /// Formats a property key as a member access suffix: <c>.key</c> for
    /// identifier-like keys, otherwise <c>["..."]</c> with the key JSON-escaped.
    /// Non-string keys render as <c>[value]</c>.
    /// </summary>
    public static string FormatProperty(object key)
    {
        if (key is not string text) return $"[{key}]";
        if (Identifier.IsMatch(text)) return "." + text;
        var escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"[\"{escaped}\"]";
    }

    private enum State
    {
        Delim,
        Upper,
        Lower,
    }

    private static string Tokenize(string source, int delimiter)
    {
        var output = new StringBuilder(source.Length);
        var state = State.Delim;
        for (var i = 0; i < source.Length; i++)
        {
            var code = (int)source[i];
            if (code is >= 65 and <= 90)
            {
                if (state == State.Upper)
                {
                    var next = i + 1 < source.Length ? (int)source[i + 1] : -1;
                    if (next is >= 97 and <= 122) output.Append((char)delimiter);
                    output.Append((char)(code + 32));
                }
                else
                {
                    if (state != State.Delim) output.Append((char)delimiter);
                    output.Append((char)(code + 32));
                }
                state = State.Upper;
            }
            else if (code is >= 97 and <= 122)
            {
                output.Append(source[i]);
                state = State.Lower;
            }
            else if (code is 45 or 95)
            {
                if (state != State.Delim) output.Append((char)delimiter);
                state = State.Delim;
            }
            else
            {
                output.Append(source[i]);
            }
        }
        return output.ToString();
    }

    private static readonly Regex CamelSeparator = new("[_-][a-z]");

    private static readonly Regex Identifier = new("^[a-z_$][\\w$]*$", RegexOptions.IgnoreCase);
}


