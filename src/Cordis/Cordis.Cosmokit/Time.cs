using System.Globalization;
using System.Text.RegularExpressions;

namespace Harness.Cordis.Cosmokit;

/// <summary>Time constants plus parsing and formatting helpers (port of cosmokit <c>time.ts</c>).</summary>
public static class Time
{
    /// <summary>One millisecond, in milliseconds.</summary>
    public const double Millisecond = 1;

    /// <summary>One second, in milliseconds.</summary>
    public const double Second = 1000;

    /// <summary>One minute, in milliseconds.</summary>
    public const double Minute = 60 * Second;

    /// <summary>One hour, in milliseconds.</summary>
    public const double Hour = 60 * Minute;

    /// <summary>One day, in milliseconds.</summary>
    public const double Day = 24 * Hour;

    /// <summary>One week, in milliseconds.</summary>
    public const double Week = 7 * Day;

    private static readonly Regex TimePattern = new(
        @"^(?:(\d+(?:\.\d+)?)w(?:eek(?:s)?)?)?(?:(\d+(?:\.\d+)?)d(?:ay(?:s)?)?)?(?:(\d+(?:\.\d+)?)h(?:our(?:s)?)?)?(?:(\d+(?:\.\d+)?)m(?:in(?:ute)?(?:s)?)?)?(?:(\d+(?:\.\d+)?)s(?:ec(?:ond)?(?:s)?)?)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a compact duration such as <c>1w2d3h4m5s</c> (each unit optional,
    /// with <c>week</c>/<c>day</c>/<c>hour</c>/<c>minute</c>/<c>second</c>
    /// spelled out accepted) into milliseconds. Returns 0 when the string does
    /// not match.
    /// </summary>
    public static double ParseTime(string source)
    {
        var match = TimePattern.Match(source);
        if (!match.Success) return 0;
        double Group(int index) => match.Groups[index].Success
            && double.TryParse(match.Groups[index].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        return Group(1) * Week + Group(2) * Day + Group(3) * Hour + Group(4) * Minute + Group(5) * Second;
    }

    /// <summary>Formats a millisecond duration compactly (<c>5d</c>, <c>3h</c>, <c>42ms</c>, ...).</summary>
    public static string Format(double ms)
    {
        var abs = Math.Abs(ms);
        if (abs >= Day - Hour / 2) return Math.Round(ms / Day) + "d";
        if (abs >= Hour - Minute / 2) return Math.Round(ms / Hour) + "h";
        if (abs >= Minute - Second / 2) return Math.Round(ms / Minute) + "m";
        if (abs >= Second) return Math.Round(ms / Second) + "s";
        return ms + "ms";
    }

    /// <summary>Zero-pads a number to <paramref name="length"/> digits.</summary>
    public static string ToDigits(double source, int length = 2)
        => source.ToString(CultureInfo.InvariantCulture).PadLeft(length, '0');

    /// <summary>
    /// Replaces <c>yyyy</c>, <c>yy</c>, <c>MM</c>, <c>dd</c>, <c>hh</c>,
    /// <c>mm</c>, <c>ss</c>, and <c>SSS</c> tokens in <paramref name="template"/>
    /// with fields of <paramref name="time"/>.
    /// </summary>
    public static string Template(string template, DateTime time)
    {
        return template
            .Replace("yyyy", time.Year.ToString(CultureInfo.InvariantCulture))
            .Replace("yy", (time.Year % 100).ToString("00", CultureInfo.InvariantCulture))
            .Replace("MM", ToDigits(time.Month))
            .Replace("dd", ToDigits(time.Day))
            .Replace("hh", ToDigits(time.Hour))
            .Replace("mm", ToDigits(time.Minute))
            .Replace("ss", ToDigits(time.Second))
            .Replace("SSS", ToDigits(time.Millisecond, 3));
    }
}
