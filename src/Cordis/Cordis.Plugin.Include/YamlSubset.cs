using System.Globalization;
using System.Text.Json;

namespace Cordis.Plugin.Include;

/// <summary>Error thrown when a YAML document is outside the supported subset.</summary>
public sealed class YamlParseException : Exception
{
    /// <summary>Create the error.</summary>
    public YamlParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Minimal zero-dependency YAML-subset parser sufficient for cordis.yml entry lists: nested maps,
/// lists, scalars (bare, single- and double-quoted), comments, empty flow collections, and
/// <c>!!js</c> expression scalars. Anchors, aliases, block scalars, multi-line strings, and flow
/// collections beyond <c>[]</c>/<c>{}</c> are not supported and fail loud.
/// </summary>
public static class YamlSubset
{
    /// <summary>Parse <paramref name="text"/> into dictionaries, lists, scalars, or expressions.</summary>
    public static object? Parse(string text)
    {
        var lines = Preprocess(text);
        var index = 0;
        var (value, empty) = ParseBlock(lines, ref index, 0);
        if (empty) return null;
        if (index < lines.Count)
        {
            throw new YamlParseException($"unexpected content at line {lines[index].Number}: '{lines[index].Content}'");
        }
        return value;
    }

    private sealed class Line
    {
        public required int Number { get; init; }
        public required int Indent { get; init; }
        public required string Content { get; init; }
    }

    private static List<Line> Preprocess(string text)
    {
        var lines = new List<Line>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var stripped = StripComment(raw.TrimEnd('\r'));
            var content = stripped.Trim();
            if (content.Length == 0 || content == "---") continue;
            lines.Add(new Line
            {
                Number = number,
                Indent = stripped.Length - stripped.TrimStart().Length,
                Content = content,
            });
        }
        return lines;
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }
        return line;
    }

    private static (object? Value, bool Empty) ParseBlock(List<Line> lines, ref int index, int indent)
    {
        if (index >= lines.Count || lines[index].Indent < indent) return (null, true);
        if (lines[index].Indent > indent)
        {
            throw new YamlParseException($"unexpected indentation at line {lines[index].Number}");
        }
        if (IsListItem(lines[index].Content)) return ParseList(lines, ref index, indent);
        return ParseMap(lines, ref index, indent);
    }

    private static bool IsListItem(string content) =>
        content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    private static (object?, bool) ParseMap(List<Line> lines, ref int index, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (index < lines.Count && lines[index].Indent == indent && !IsListItem(lines[index].Content))
        {
            var line = lines[index];
            var (key, rest) = SplitKey(line.Content);
            index++;
            if (rest is null)
            {
                if (index < lines.Count && lines[index].Indent > indent)
                {
                    var (child, _) = ParseBlock(lines, ref index, lines[index].Indent);
                    map[key] = child;
                }
                else
                {
                    map[key] = null;
                }
            }
            else
            {
                map[key] = ParseScalar(rest.Trim());
            }
        }
        return (map, map.Count == 0);
    }

    private static (object?, bool) ParseList(List<Line> lines, ref int index, int indent)
    {
        var list = new List<object?>();
        while (index < lines.Count && lines[index].Indent == indent && IsListItem(lines[index].Content))
        {
            var line = lines[index];
            var content = line.Content == "-" ? "" : line.Content[2..];
            index++;
            if (content.Length == 0)
            {
                if (index < lines.Count && lines[index].Indent > indent)
                {
                    var (child, _) = ParseBlock(lines, ref index, lines[index].Indent);
                    list.Add(child);
                }
                else
                {
                    list.Add(null);
                }
            }
            else if (TrySplitKey(content, out var key, out var rest))
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (rest is null)
                {
                    if (index < lines.Count && lines[index].Indent > indent)
                    {
                        var (child, _) = ParseBlock(lines, ref index, lines[index].Indent);
                        map[key] = child;
                    }
                    else
                    {
                        map[key] = null;
                    }
                }
                else
                {
                    map[key] = ParseScalar(rest.Trim());
                }
                // Deeper non-list lines extend this same map item (the `config:` continuation).
                if (index < lines.Count && lines[index].Indent > indent && !IsListItem(lines[index].Content))
                {
                    var (extra, _) = ParseMap(lines, ref index, lines[index].Indent);
                    foreach (var pair in (Dictionary<string, object?>)extra!) map[pair.Key] = pair.Value;
                }
                list.Add(map);
            }
            else
            {
                list.Add(ParseScalar(content));
            }
        }
        return (list, list.Count == 0);
    }

    private static KeyValuePair<string, string?> SplitKey(string content)
    {
        var colon = content.IndexOf(':');
        if (colon < 0) throw new YamlParseException($"expected 'key: value' but got '{content}'");
        var key = content[..colon].Trim();
        if (key.Length == 0) throw new YamlParseException($"empty key in '{content}'");
        var rest = content[(colon + 1)..].Trim();
        return new KeyValuePair<string, string?>(key, rest.Length == 0 ? null : rest);
    }

    private static bool TrySplitKey(string content, out string key, out string? rest)
    {
        var colon = content.IndexOf(':');
        if (colon <= 0 || colon >= content.Length - 1 || content[colon + 1] != ' ')
        {
            key = "";
            rest = null;
            return false;
        }
        key = content[..colon].Trim();
        rest = content[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>Parse one scalar (shared with the entry-list conversion for inline values).</summary>
    public static object? ParseScalar(string text)
    {
        if (text.StartsWith("!!js", StringComparison.Ordinal))
        {
            var expression = text[4..].Trim();
            if (expression.Length == 0) throw new YamlParseException("!!js requires an expression");
            return new ConfigExpression(expression);
        }
        switch (text)
        {
            case "null":
            case "Null":
            case "NULL":
            case "~":
                return null;
            case "true":
            case "True":
            case "TRUE":
                return true;
            case "false":
            case "False":
            case "FALSE":
                return false;
            case "[]":
                return new List<object?>();
            case "{}":
                return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return Unescape(text[1..^1]);
        }
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1];
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        return text;
    }

    private static string Unescape(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i + 1 >= text.Length)
            {
                builder.Append(ch);
                continue;
            }
            var escaped = text[++i];
            builder.Append(escaped switch
            {
                'n' => '\n',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                _ => escaped,
            });
        }
        return builder.ToString();
    }

    /// <summary>Parse a JSON document through the same shapes (entry lists may be .json files).</summary>
    public static object? ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return ConvertElement(document.RootElement);
    }

    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ConvertElement(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
