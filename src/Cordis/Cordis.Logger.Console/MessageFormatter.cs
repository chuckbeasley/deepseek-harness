using Harness.Cordis.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Harness.Cordis.Logger.Console;

/// <summary>
/// Message body rendering (port of the TS <c>Logger.format</c> with the default placeholder
/// formatters): an exception argument renders its full text, a printf-style
/// <c>%s</c>/<c>%d</c>/<c>%i</c>/<c>%f</c>/<c>%o</c>/<c>%O</c>/<c>%c</c>/<c>%C</c> format string
/// resolves placeholders against the remaining arguments (unknown specifiers pass through), and
/// each line is truncated to the configured maximum length with a <c>...</c> suffix.
/// </summary>
internal static class MessageFormatter
{
    /// <summary>Render the message body (no level prefix, label, or timestamp).</summary>
    public static string Format(LogMessage message, int maxLength, bool colors)
    {
        var args = new List<object?>(message.Args);
        if (args.Count > 0 && args[0] is Exception error)
        {
            args[0] = error.ToString(); // TS: error.stack || error.message
            args.Insert(0, "%s");
        }
        else if (args.Count == 0 || args[0] is not string)
        {
            args.Insert(0, "%o");
        }

        var format = (string)args[0]!;
        args.RemoveAt(0);
        var body = new StringBuilder(format.Length + 16);
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch == '%' && i + 1 < format.Length)
            {
                var spec = format[i + 1];
                if (spec == '%')
                {
                    body.Append('%');
                    i++;
                    continue;
                }
                if (char.IsLetter(spec) && args.Count > 0 && HasFormatter(spec))
                {
                    body.Append(Apply(spec, args[0], message, colors));
                    args.RemoveAt(0);
                    i++;
                    continue;
                }
            }
            body.Append(ch);
        }
        foreach (var arg in args)
        {
            body.Append(' ').Append(RenderLeftover(arg));
        }

        return string.Join("\n", body.ToString()
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => line.Length > maxLength ? line[..maxLength] + "..." : line));
    }

    private static bool HasFormatter(char spec) => spec is 's' or 'd' or 'i' or 'f' or 'o' or 'O' or 'c' or 'C';

    private static string Apply(char spec, object? value, LogMessage message, bool colors)
    {
        return spec switch
        {
            's' => value?.ToString() ?? "",
            'd' or 'i' => Math.Truncate(ToNumber(value)).ToString(CultureInfo.InvariantCulture),
            'f' => ToNumber(value).ToString(CultureInfo.InvariantCulture),
            'o' or 'O' => RenderObject(value),
            'c' => "",
            'C' => colors
                ? AnsiColor.Wrap(value?.ToString() ?? "", AnsiColor.NameCode(message.Name), bold: false)
                : value?.ToString() ?? "",
            _ => "",
        };
    }

    private static double ToNumber(object? value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            decimal m => (double)m,
            bool b => b ? 1 : 0,
            null => 0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
            _ => double.NaN,
        };
    }

    /// <summary>JSON rendering used by <c>%o</c>/<c>%O</c> and for object leftovers (TS <c>JSON.stringify</c>).</summary>
    private static string RenderObject(object? value)
    {
        if (value is null) return "null";
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            return value.ToString() ?? "";
        }
    }

    /// <summary>String rendering for leftover arguments (TS: objects serialize, everything else stringifies).</summary>
    private static string RenderLeftover(object? value)
    {
        if (value is null) return "null";
        if (value is string s) return s;
        return RenderObject(value);
    }
}
