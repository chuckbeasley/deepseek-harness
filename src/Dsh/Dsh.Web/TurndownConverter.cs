using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Web;

/// <summary>
/// Faithful C# port of the shared HTML→markdown converter (turndown 7 over its bundled domino
/// DOM, plus the repo's GitHub-flavored table rules and non-visible-content removal). The rule
/// set, whitespace folding, escaping, and joining match turndown byte-for-byte for the recorded
/// corpus page set; the port covers the core rules (headings, paragraphs, lists, links, emphasis,
/// code, rules, images, blockquotes) plus the custom table and removal rules. The instance is
/// stateless across converts and safe to share.
/// </summary>
public sealed class TurndownConverter
{
    private const string HeadingStyleAtx = "atx";
    private const string BulletListMarker = "-";
    private const string EmDelimiter = "_";
    private const string StrongDelimiter = "**";
    private const string Br = "\n\n";

    private static readonly string[] BlockElements =
    {
        "ADDRESS", "ARTICLE", "ASIDE", "AUDIO", "BLOCKQUOTE", "BODY", "CANVAS",
        "CENTER", "DD", "DIR", "DIV", "DL", "DT", "FIELDSET", "FIGCAPTION", "FIGURE",
        "FOOTER", "FORM", "FRAMESET", "H1", "H2", "H3", "H4", "H5", "H6", "HEADER",
        "HGROUP", "HR", "HTML", "ISINDEX", "LI", "MAIN", "MENU", "NAV", "NOFRAMES",
        "NOSCRIPT", "OL", "OUTPUT", "P", "PRE", "SECTION", "TABLE", "TBODY", "TD",
        "TFOOT", "TH", "THEAD", "TR", "UL",
    };

    private static readonly string[] VoidElements =
    {
        "AREA", "BASE", "BR", "COL", "COMMAND", "EMBED", "HR", "IMG", "INPUT",
        "KEYGEN", "LINK", "META", "PARAM", "SOURCE", "TRACK", "WBR",
    };

    private static readonly string[] MeaningfulWhenBlankElements =
    {
        "A", "TABLE", "THEAD", "TBODY", "TFOOT", "TH", "TD", "IFRAME", "SCRIPT", "AUDIO", "VIDEO",
    };

    private static readonly HashSet<string> BlockSet = new(BlockElements, StringComparer.Ordinal);
    private static readonly HashSet<string> VoidSet = new(VoidElements, StringComparer.Ordinal);
    private static readonly HashSet<string> MeaningfulBlankSet = new(MeaningfulWhenBlankElements, StringComparer.Ordinal);

    private static readonly string[] RemoveNonVisibleTags =
    {
        "SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "IFRAME", "OBJECT", "EMBED",
    };

    private sealed record Rule(
        Func<HtmlNode, bool> Filter,
        Func<string, HtmlNode, string> Replacement,
        Func<string>? Append = null);

    private readonly List<Rule> _rules = new();

    /// <summary>Create the shared converter with the recorded rule configuration.</summary>
    public TurndownConverter()
    {
        AddRule(new Rule(RemoveNonVisibleContent, (_, _) => string.Empty));
        AddRule(new Rule(TableCellWithoutSpanExpansion, TableCellReplacement));
        AddRule(new Rule(TableRowWithoutSpanExpansion, TableRowReplacement));
        AddRule(new Rule(node => node.NodeName == "TABLE", TableReplacement));
        AddRule(new Rule(node => node.NodeName == "P", (content, _) => "\n\n" + content + "\n\n"));
        AddRule(new Rule(node => node.NodeName == "BR", (_, _) => Br + "\n"));
        AddRule(new Rule(node => node.NodeName.Length == 2 && node.NodeName[0] == 'H' && node.NodeName[1] >= '1' && node.NodeName[1] <= '6',
            (content, node) => "\n\n" + new string('#', node.NodeName[1] - '0') + " " + content + "\n\n"));
        AddRule(new Rule(node => node.NodeName == "BLOCKQUOTE", (content, _) =>
        {
            content = Regex.Replace(content, @"^\n+|\n+$", string.Empty);
            content = Regex.Replace(content, "^", "> ", RegexOptions.Multiline);
            return "\n\n" + content + "\n\n";
        }));
        AddRule(new Rule(node => node.NodeName is "UL" or "OL", (content, node) =>
        {
            var parent = node.Parent;
            if (parent is not null && parent.NodeName == "LI" && ReferenceEquals(parent.LastElementChild, node)) return "\n" + content;
            return "\n\n" + content + "\n\n";
        }));
        AddRule(new Rule(node => node.NodeName == "LI", (content, node) =>
        {
            content = Regex.Replace(content, @"^\n+", string.Empty);
            content = Regex.Replace(content, @"\n+$", "\n");
            content = Regex.Replace(content, "\n", "\n    ");
            var prefix = BulletListMarker + "   ";
            var parent = node.Parent;
            if (parent is not null && parent.NodeName == "OL")
            {
                var start = parent.GetAttribute("start");
                var index = parent.Children.FindIndex(child => ReferenceEquals(child, node));
                prefix = (start is not null && int.TryParse(start, out var startNumber) ? startNumber + index : index + 1) + ".  ";
            }
            var trailing = node.NextSibling is not null && !Regex.IsMatch(content, @"\n$") ? "\n" : string.Empty;
            return prefix + content + trailing;
        }));
        AddRule(new Rule(node => node.NodeName == "HR", (_, _) => "\n\n* * *\n\n"));
        AddRule(new Rule(node => node.NodeName == "A" && node.GetAttribute("href") is not null, (content, node) =>
        {
            var href = node.GetAttribute("href") ?? string.Empty;
            href = Regex.Replace(href, @"([()])", "\\$1");
            var title = CleanAttribute(node.GetAttribute("title"));
            if (title.Length > 0) title = " \"" + Regex.Replace(title, "\"", "\\\"") + "\"";
            return "[" + content + "](" + href + title + ")";
        }));
        AddRule(new Rule(node => node.NodeName is "EM" or "I", (content, _) =>
            content.Trim().Length == 0 ? string.Empty : EmDelimiter + content + EmDelimiter));
        AddRule(new Rule(node => node.NodeName is "STRONG" or "B", (content, _) =>
            content.Trim().Length == 0 ? string.Empty : StrongDelimiter + content + StrongDelimiter));
        AddRule(new Rule(node => node.NodeName == "CODE" && !IsCodeBlock(node), (content, _) =>
        {
            if (content.Length == 0) return string.Empty;
            content = Regex.Replace(content, @"\r?\n|\r", " ");
            var extraSpace = Regex.IsMatch(content, @"^`|^ .*?[^ ].* $|`$") ? " " : string.Empty;
            var delimiter = "`";
            while (Regex.IsMatch(content, Regex.Escape(delimiter) + "+")) delimiter += "`";
            return delimiter + extraSpace + content + extraSpace + delimiter;
        }));
        AddRule(new Rule(node => node.NodeName == "IMG", (_, node) =>
        {
            var alt = CleanAttribute(node.GetAttribute("alt"));
            var src = node.GetAttribute("src") ?? string.Empty;
            var title = CleanAttribute(node.GetAttribute("title"));
            var titlePart = title.Length > 0 ? " \"" + title + "\"" : string.Empty;
            return src.Length > 0 ? "![" + alt + "](" + src + titlePart + ")" : string.Empty;
        }));
    }

    /// <summary>Convert an HTML source string to markdown (the turndown entry point).</summary>
    public string Convert(string html)
    {
        if (html.Length == 0) return string.Empty;
        var root = HtmlDom.ParseRoot("<x-turndown id=\"turndown-root\">" + html + "</x-turndown>");
        CollapseWhitespace(root);
        var output = Process(root);
        foreach (var rule in _rules)
        {
            if (rule.Append is not null) output = Join(output, rule.Append());
        }
        return Regex.Replace(output, @"^[\t\r\n]+", string.Empty)
            .ReplaceRegex(@"[\t\r\n\s]+$", string.Empty);
    }

    private void AddRule(Rule rule) => _rules.Add(rule);

    private bool RemoveNonVisibleContent(HtmlNode node)
    {
        if (RemoveNonVisibleTags.Contains(node.NodeName)) return true;
        if (node.HasAttribute("hidden")) return true;
        var ariaHidden = node.GetAttribute("aria-hidden");
        if (ariaHidden is not null && ariaHidden.ToLowerInvariant() == "true") return true;
        if (node.NodeName == "INPUT")
        {
            var type = node.GetAttribute("type");
            if (type is not null && type.ToLowerInvariant() == "hidden") return true;
        }
        var style = node.GetAttribute("style");
        if (style is null) return false;
        foreach (var declaration in style.Split(';'))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0) continue;
            var property = declaration[..separator].Trim().ToLowerInvariant();
            var value = declaration[(separator + 1)..].Trim().ToLowerInvariant();
            value = Regex.Replace(value, @"\s*!important\s*$", string.Empty);
            if (property == "display" && value == "none") return true;
            if (property == "visibility" && (value == "hidden" || value == "collapse")) return true;
        }
        return false;
    }

    private bool TableCellWithoutSpanExpansion(HtmlNode node)
        => node.NodeName is "TH" or "TD";

    private string TableCellReplacement(string content, HtmlNode node)
    {
        var row = node.Parent;
        var index = row is null ? 0 : row.Children.FindIndex(child => ReferenceEquals(child, node));
        var prefix = index == 0 ? "| " : " ";
        var escaped = content.Trim()
            .Replace("\n\r", "<br>")
            .Replace("\n", "<br>");
        escaped = Regex.Replace(escaped, @"\|+", "\\|");
        escaped = escaped.PadRight(3, ' ');
        return prefix + escaped + " |";
    }

    private bool TableRowWithoutSpanExpansion(HtmlNode node) => node.NodeName == "TR";

    private string TableRowReplacement(string content, HtmlNode node)
    {
        var border = string.Empty;
        if (IsTableHeadingRow(node))
        {
            var builder = new StringBuilder();
            var cells = node.Children.Where(child => child.IsElement && child.NodeName is "TH" or "TD").ToArray();
            for (var index = 0; index < cells.Length; index++)
            {
                builder.Append(TableCellReplacement(TableBorder(cells[index]), cells[index]));
            }
            border = builder.ToString();
        }
        return "\n" + content + (border.Length > 0 ? "\n" + border : string.Empty);
    }

    private static bool IsTableHeadingRow(HtmlNode row)
    {
        var cells = row.Children.Where(child => child.IsElement && child.NodeName is "TH" or "TD").ToArray();
        var section = row.Parent;
        var table = section?.Parent;
        var isFirstRow = table is not null && ReferenceEquals(table.Children.FirstOrDefault(child => child.IsElement), row);
        return (section is not null && section.NodeName == "THEAD" || isFirstRow)
            && cells.Length > 0 && cells.All(cell => cell.NodeName == "TH");
    }

    /// <summary>The GFM table rule: collapse blank lines, synthesize a divider-only header when the
    /// table has no heading row, and carry the caption (port of the gfm plugin's table rule).</summary>
    private static string TableReplacement(string content, HtmlNode node)
    {
        if (TableCellHasBlockContent(node)) return content;
        content = Regex.Replace(content, @"\n+", "\n");
        var lines = content.Trim().Split('\n');
        var secondLine = lines.Length >= 2 ? lines[1] : null;
        var secondLineIsDivider = secondLine is not null && Regex.IsMatch(secondLine, @"\| :?---");
        var columnCount = TableColumnCount(node);
        var emptyHeader = string.Empty;
        if (columnCount > 0 && !secondLineIsDivider)
        {
            var builder = new StringBuilder("|");
            for (var i = 0; i < columnCount; i++) builder.Append("     |");
            builder.Append('\n').Append('|');
            for (var i = 0; i < columnCount; i++)
            {
                builder.Append(' ').Append(ColumnBorder(node, i)).Append(" |");
            }
            emptyHeader = builder.ToString();
        }
        var caption = node.GetElementsByTagName("CAPTION").FirstOrDefault() is { } captionNode
            ? captionNode.TextContent ?? string.Empty
            : string.Empty;
        var captionPrefix = caption.Length > 0 ? caption + "\n\n" : string.Empty;
        var tableContent = (emptyHeader + content).TrimStart();
        return "\n\n" + captionPrefix + tableContent + "\n\n";
    }

    /// <summary>Whether any cell contains block content that would make a valid GFM table impossible.</summary>
    private static bool TableCellHasBlockContent(HtmlNode table)
    {
        foreach (var cell in table.GetElementsByTagName("TD").Concat(table.GetElementsByTagName("TH")))
        {
            if (cell.Children.Any(child => child.IsElement && IsBlock(child))) return true;
        }
        return false;
    }

    /// <summary>The widest row's cell count (the gfm tableColCount).</summary>
    private static int TableColumnCount(HtmlNode table)
    {
        var max = 0;
        foreach (var row in table.GetElementsByTagName("TR"))
        {
            var count = row.Children.Count(child => child.IsElement && child.NodeName is "TH" or "TD");
            if (count > max) max = count;
        }
        return max;
    }

    /// <summary>The column's majority alignment border (the gfm getColumnAlignment/getBorder).</summary>
    private static string ColumnBorder(HtmlNode table, int columnIndex)
    {
        var votes = new Dictionary<string, int>(StringComparer.Ordinal) { ["left"] = 0, ["right"] = 0, ["center"] = 0, [""] = 0 };
        var align = string.Empty;
        foreach (var row in table.GetElementsByTagName("TR"))
        {
            var cells = row.Children.Where(child => child.IsElement && child.NodeName is "TH" or "TD").ToArray();
            if (columnIndex < cells.Length)
            {
                var cellAlignment = CellAlignment(cells[columnIndex]);
                votes[cellAlignment] = votes.GetValueOrDefault(cellAlignment) + 1;
                if (votes[cellAlignment] > votes.GetValueOrDefault(align)) align = cellAlignment;
            }
        }
        return align switch
        {
            "left" => ":---",
            "right" => "---:",
            "center" => ":---:",
            _ => "---",
        };
    }

    private static string CellAlignment(HtmlNode cell)
    {
        var alignment = (cell.GetAttribute("align") ?? string.Empty).ToLowerInvariant();
        if (alignment.Length == 0)
        {
            var style = cell.GetAttribute("style") ?? string.Empty;
            var match = Regex.Match(style, @"text-align\s*:\s*([a-z]+)", RegexOptions.IgnoreCase);
            if (match.Success) alignment = match.Groups[1].Value.ToLowerInvariant();
        }
        return alignment;
    }

    private static string TableBorder(HtmlNode cell)
    {
        var alignment = (cell.GetAttribute("align") ?? string.Empty).ToLowerInvariant();
        if (alignment.Length == 0)
        {
            var style = cell.GetAttribute("style") ?? string.Empty;
            var match = Regex.Match(style, @"text-align\s*:\s*([a-z]+)", RegexOptions.IgnoreCase);
            if (match.Success) alignment = match.Groups[1].Value.ToLowerInvariant();
        }
        return alignment switch
        {
            "left" => ":---",
            "right" => "---:",
            "center" => ":---:",
            _ => "---",
        };
    }

    private static bool IsCodeBlock(HtmlNode node)
    {
        var hasSiblings = node.PreviousSibling is not null || node.NextSibling is not null;
        return node.Parent is not null && node.Parent.NodeName == "PRE" && !hasSiblings;
    }

    private static string CleanAttribute(string? attribute)
        => attribute is null ? string.Empty : Regex.Replace(attribute, @"(\n+\s*)+", "\n");

    private static bool IsBlock(HtmlNode node) => BlockSet.Contains(node.NodeName);

    private static bool IsVoid(HtmlNode node) => VoidSet.Contains(node.NodeName);

    private static bool HasVoid(HtmlNode node)
        => VoidElements.Any(name => node.GetElementsByTagName(name).Any());

    private static bool IsMeaningfulWhenBlank(HtmlNode node) => MeaningfulBlankSet.Contains(node.NodeName);

    private static bool HasMeaningfulWhenBlank(HtmlNode node)
        => MeaningfulWhenBlankElements.Any(name => node.GetElementsByTagName(name).Any());

    private static bool IsBlank(HtmlNode node)
        => !IsVoid(node)
            && !IsMeaningfulWhenBlank(node)
            && Regex.IsMatch(node.TextContent, @"^\s*$", RegexOptions.IgnoreCase)
            && !HasVoid(node)
            && !HasMeaningfulWhenBlank(node);

    private static string Escape(string text)
    {
        var output = text;
        output = output.Replace("\\", "\\\\");
        output = output.Replace("*", "\\*");
        output = Regex.Replace(output, "^-", "\\-");
        output = Regex.Replace(output, "^\\+ ", "\\+ ");
        output = Regex.Replace(output, "^(=+)", "\\$1");
        output = Regex.Replace(output, "^(#{1,6}) ", "\\$1 ");
        output = output.Replace("`", "\\`");
        output = Regex.Replace(output, "^~~~", "\\~~~");
        output = output.Replace("[", "\\[");
        output = output.Replace("]", "\\]");
        output = Regex.Replace(output, "^>", "\\>");
        output = output.Replace("_", "\\_");
        output = Regex.Replace(output, "^(\\d+)\\. ", "$1\\. ");
        return output;
    }

    private static readonly Regex EdgeWhitespaceRegex = new(
        @"^(([ \t\r\n]*)(\s*))(?:(?=\S)[\s\S]*\S)?((\s*?)([ \t\r\n]*))$", RegexOptions.CultureInvariant);

    private static (string Leading, string LeadingAscii, string LeadingNonAscii, string Trailing, string TrailingNonAscii, string TrailingAscii) EdgeWhitespace(string text)
    {
        var match = EdgeWhitespaceRegex.Match(text);
        if (!match.Success)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }
        return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value,
            match.Groups[4].Value, match.Groups[5].Value, match.Groups[6].Value);
    }

    private static bool IsFlankedByWhitespace(string side, HtmlNode node)
    {
        var sibling = side == "left" ? node.PreviousSibling : node.NextSibling;
        var regExp = side == "left" ? new Regex(@" $") : new Regex(@"^ ");
        if (sibling is null) return false;
        if (sibling.IsText) return regExp.IsMatch(sibling.Text ?? string.Empty);
        if (sibling.IsElement && !IsBlock(sibling)) return regExp.IsMatch(sibling.TextContent);
        return false;
    }

    private static (string Leading, string Trailing) FlankingWhitespace(HtmlNode node)
    {
        if (IsBlock(node)) return (string.Empty, string.Empty);
        var edges = EdgeWhitespace(node.TextContent);
        if (edges.LeadingAscii.Length > 0 && IsFlankedByWhitespace("left", node)) edges.Leading = edges.LeadingNonAscii;
        if (edges.TrailingAscii.Length > 0 && IsFlankedByWhitespace("right", node)) edges.Trailing = edges.TrailingNonAscii;
        return (edges.Leading, edges.Trailing);
    }

    private string Process(HtmlNode parentNode)
    {
        var output = string.Empty;
        foreach (var child in parentNode.Children)
        {
            var replacement = string.Empty;
            if (child.IsText)
            {
                replacement = Escape(child.Text ?? string.Empty);
            }
            else if (child.IsElement)
            {
                replacement = ReplacementForNode(child);
            }
            output = Join(output, replacement);
        }
        return output;
    }

    private string ReplacementForNode(HtmlNode node)
    {
        var rule = RuleForNode(node);
        var content = Process(node);
        var whitespace = FlankingWhitespace(node);
        if (whitespace.Leading.Length > 0 || whitespace.Trailing.Length > 0) content = content.Trim();
        return whitespace.Leading + rule.Replacement(content, node) + whitespace.Trailing;
    }

    private Rule RuleForNode(HtmlNode node)
    {
        if (IsBlank(node)) return new Rule(_ => false, (_, n) => IsBlock(n) ? "\n\n" : string.Empty);
        foreach (var rule in _rules)
        {
            if (rule.Filter(node)) return rule;
        }
        return new Rule(_ => false, (content, n) => IsBlock(n) ? "\n\n" + content + "\n\n" : content);
    }

    /// <summary>The collapse-whitespace pre-pass (adapted from collapse-whitespace by Luc Thevenard).</summary>
    private static void CollapseWhitespace(HtmlNode element)
    {
        if (element.FirstChild is null || element.NodeName == "PRE") return;
        HtmlNode? prevText = null;
        var keepLeadingWs = false;
        HtmlNode? prev = null;
        var node = Next(prev, element);
        while (!ReferenceEquals(node, element))
        {
            if (node.IsText)
            {
                var text = Regex.Replace(node.Text ?? string.Empty, @"[ \r\n\t]+", " ");
                if ((prevText is null || (prevText.Text ?? string.Empty).EndsWith(" ", StringComparison.Ordinal))
                    && !keepLeadingWs && text.StartsWith(" ", StringComparison.Ordinal))
                {
                    text = text[1..];
                }
                if (text.Length == 0)
                {
                    node = Remove(node);
                    continue;
                }
                node.Text = text;
                prevText = node;
            }
            else if (node.IsElement)
            {
                if (IsBlock(node) || node.NodeName == "BR")
                {
                    if (prevText is not null)
                    {
                        prevText.Text = (prevText.Text ?? string.Empty).ReplaceRegex(" $", string.Empty);
                    }
                    prevText = null;
                    keepLeadingWs = false;
                }
                else if (IsVoid(node) || node.NodeName == "PRE")
                {
                    prevText = null;
                    keepLeadingWs = true;
                }
                else if (prevText is not null)
                {
                    keepLeadingWs = false;
                }
            }
            else
            {
                node = Remove(node);
                continue;
            }
            var nextNode = Next(prev, node);
            prev = node;
            node = nextNode;
        }
        if (prevText is not null)
        {
            prevText.Text = (prevText.Text ?? string.Empty).ReplaceRegex(" $", string.Empty);
            if ((prevText.Text ?? string.Empty).Length == 0) Remove(prevText);
        }
    }

    private static HtmlNode Remove(HtmlNode node)
    {
        var next = node.NextSibling ?? node.Parent ?? node;
        node.Parent?.RemoveChild(node);
        return next;
    }

    private static HtmlNode Next(HtmlNode? prev, HtmlNode current)
    {
        if ((prev is not null && ReferenceEquals(prev.Parent, current)) || current.NodeName == "PRE")
        {
            return current.NextSibling ?? current.Parent ?? current;
        }
        return current.FirstChild ?? current.NextSibling ?? current.Parent ?? current;
    }

    private static string Join(string output, string replacement)
    {
        var s1 = TrimTrailingNewlines(output);
        var s2 = Regex.Replace(replacement, @"^\n*", string.Empty);
        var nls = Math.Max(output.Length - s1.Length, replacement.Length - s2.Length);
        var separator = nls >= 2 ? "\n\n" : nls == 1 ? "\n" : string.Empty;
        return s1 + separator + s2;
    }

    private static string TrimTrailingNewlines(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '\n') end--;
        return text[..end];
    }
}

internal static class TurndownExtensions
{
    /// <summary>Replace the first match (the JS <c>String.replace</c> with a string pattern).</summary>
    public static string ReplaceRegex(this string text, string pattern, string replacement)
        => Regex.Replace(text, pattern, replacement, RegexOptions.None, TimeSpan.FromSeconds(5));
}