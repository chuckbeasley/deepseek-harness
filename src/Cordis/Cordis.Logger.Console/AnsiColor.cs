using System.Globalization;

namespace Harness.Cordis.Logger.Console;

/// <summary>
/// ANSI color helpers ported from the TS <c>Logger.color</c>/<c>Logger.code</c>: a deterministic
/// palette code for a logger name and the SGR escape sequence wrapping a value. Only used when
/// <see cref="ConsoleExporterConfig.Colors"/> is enabled; the default output carries no escape
/// sequences.
/// </summary>
internal static class AnsiColor
{
    private static readonly int[] C16 = { 6, 2, 3, 4, 5, 1 };

    private static readonly int[] C256 =
    {
        20, 21, 26, 27, 32, 33, 38, 39, 40, 41, 42, 43, 44, 45, 56, 57, 62,
        63, 68, 69, 74, 75, 76, 77, 78, 79, 80, 81, 92, 93, 98, 99, 112, 113,
        129, 134, 135, 148, 149, 160, 161, 162, 163, 164, 165, 166, 167, 168,
        169, 170, 171, 172, 173, 178, 179, 184, 185, 196, 197, 198, 199, 200,
        201, 202, 203, 204, 205, 206, 207, 208, 209, 214, 215, 220, 221,
    };

    /// <summary>Wrap <paramref name="value"/> in the SGR sequence for <paramref name="code"/> (port of <c>Logger.color</c>).</summary>
    public static string Wrap(string value, int code, bool bold)
    {
        var codeText = code < 8 ? code.ToString(CultureInfo.InvariantCulture) : $"8;5;{code}";
        var decoration = bold ? ";1" : "";
        return $"\u001b[3{codeText}{decoration}m{value}\u001b[0m";
    }

    /// <summary>Deterministic palette code for a logger name (port of <c>Logger.code</c> using the 256-color palette).</summary>
    public static int NameCode(string name)
    {
        int hash = 0;
        foreach (var ch in name)
        {
            hash = ((hash << 3) - hash) + ch + 13;
        }
        return C256[Math.Abs((long)hash) % C256.Length];
    }
}
