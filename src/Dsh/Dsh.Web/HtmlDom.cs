namespace Dsh.Web;

/// <summary>
/// Minimal HTML DOM for the turndown port (the subset domino provides for converted pages): a
/// tree of element and text nodes with the traversal, attribute, and entity behavior the
/// converter relies on. The parser is deliberately small — well-formed pages with quoted
/// attributes, void elements, and raw-text script/style bodies — the corpus page set.
/// </summary>
internal sealed class HtmlNode
{
    public HtmlNode? Parent { get; set; }
    public List<HtmlNode> Children { get; } = new();

    public string NodeName { get; init; } = string.Empty;
    public string? Text { get; set; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    public bool IsElement => NodeName.Length > 0;
    public bool IsText => Text is not null;

    public HtmlNode? FirstChild => Children.Count > 0 ? Children[0] : null;
    public HtmlNode? LastElementChild => Children.LastOrDefault(child => child.IsElement);
    public HtmlNode? NextSibling
    {
        get
        {
            var parent = Parent;
            if (parent is null) return null;
            var index = parent.Children.IndexOf(this);
            return index >= 0 && index + 1 < parent.Children.Count ? parent.Children[index + 1] : null;
        }
    }
    public HtmlNode? PreviousSibling
    {
        get
        {
            var parent = Parent;
            if (parent is null) return null;
            var index = parent.Children.IndexOf(this);
            return index > 0 ? parent.Children[index - 1] : null;
        }
    }

    public string TextContent
    {
        get
        {
            if (IsText) return Text ?? string.Empty;
            var builder = new System.Text.StringBuilder();
            foreach (var child in Children) builder.Append(child.TextContent);
            return builder.ToString();
        }
    }

    public string? GetAttribute(string name)
        => Attributes.TryGetValue(name, out var value) ? value : null;

    public bool HasAttribute(string name) => Attributes.ContainsKey(name);

    public IEnumerable<HtmlNode> GetElementsByTagName(string name)
    {
        foreach (var child in Children)
        {
            if (child.IsElement && string.Equals(child.NodeName, name, StringComparison.OrdinalIgnoreCase)) yield return child;
            foreach (var descendant in child.GetElementsByTagName(name)) yield return descendant;
        }
    }

    public void RemoveChild(HtmlNode child)
    {
        Children.Remove(child);
        child.Parent = null;
    }
}

/// <summary>Parse one HTML source string into the element tree (the turndown root wrapper).</summary>
internal static class HtmlDom
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "textarea", "title",
    };

    /// <summary>Parse <paramref name="html"/> inside a synthetic root element (the turndown wrapping).</summary>
    public static HtmlNode ParseRoot(string html)
    {
        var root = new HtmlNode { NodeName = "X-TURNDOWN" };
        var current = root;
        var index = 0;
        while (index < html.Length)
        {
            var next = html.IndexOf('<', index);
            if (next < 0)
            {
                AppendText(current, html[index..]);
                break;
            }
            if (next > index) AppendText(current, html[index..next]);
            if (next + 1 >= html.Length) break;
            if (html[next + 1] == '!')
            {
                // Comments and doctypes carry no content; skip through their tag end.
                var bangEnd = FindTagEnd(html, next);
                index = bangEnd < 0 ? html.Length : bangEnd + 1;
                continue;
            }
            var tagEnd = FindTagEnd(html, next);
            if (tagEnd < 0) break;
            var tag = html[(next + 1)..tagEnd];
            index = tagEnd + 1;
            if (tag.Length == 0) continue;
            if (tag[0] == '/')
            {
                var name = tag[1..].Trim();
                if (current.Parent is not null && string.Equals(current.NodeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    current = current.Parent;
                }
                else if (current.Parent is not null)
                {
                    // Mismatched close: walk up to the matching open element, or the root.
                    var ancestor = current.Parent;
                    while (ancestor.Parent is not null && !string.Equals(ancestor.NodeName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        ancestor = ancestor.Parent;
                    }
                    current = ancestor;
                }
                continue;
            }
            var (tagName, attributes, selfClosing) = ParseTag(tag);
            if (tagName.Length == 0) continue;
            var element = new HtmlNode { NodeName = tagName.ToUpperInvariant() };
            foreach (var pair in attributes) element.Attributes[pair.Key] = pair.Value;
            current.Children.Add(element);
            element.Parent = current;
            if (selfClosing || VoidElements.Contains(tagName)) continue;
            if (RawTextElements.Contains(tagName))
            {
                var bodyStart = index;
                var bodyEnd = FindRawTextEnd(html, tagName, bodyStart);
                if (bodyEnd < 0)
                {
                    AppendText(element, html[bodyStart..]);
                    index = html.Length;
                    break;
                }
                AppendText(element, html[bodyStart..bodyEnd]);
                index = bodyEnd;
                // The closing tag is consumed by the next loop iteration's close-tag arm.
                if (current.Parent is not null)
                {
                    element.Parent = current;
                    current = element;
                }
                continue;
            }
            current = element;
        }
        return root;
    }

    private static void AppendText(HtmlNode parent, string raw)
    {
        if (raw.Length == 0) return;
        var text = DecodeEntities(raw);
        parent.Children.Add(new HtmlNode { Text = text, Parent = parent });
    }

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    private static int FindRawTextEnd(string html, string name, int from)
    {
        var prefix = "</" + name;
        var candidate = html.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
        while (candidate >= 0)
        {
            var after = candidate + prefix.Length;
            if (after >= html.Length || html[after] == '>' || html[after] == '/' || char.IsWhiteSpace(html[after])) return candidate;
            candidate = html.IndexOf(prefix, candidate + 1, StringComparison.OrdinalIgnoreCase);
        }
        return -1;
    }

    private static (string Name, List<(string Key, string Value)> Attributes, bool SelfClosing) ParseTag(string tag)
    {
        var name = new System.Text.StringBuilder();
        var i = 0;
        while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '/')
        {
            name.Append(tag[i]);
            i++;
        }
        var attributes = new List<(string, string)>();
        var selfClosing = false;
        while (i < tag.Length)
        {
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            if (i >= tag.Length) break;
            if (tag[i] == '/')
            {
                selfClosing = true;
                break;
            }
            var keyStart = i;
            while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '=' && tag[i] != '/') i++;
            var key = tag[keyStart..i];
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            var value = string.Empty;
            if (i < tag.Length && tag[i] == '=')
            {
                i++;
                while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
                if (i < tag.Length && (tag[i] == '"' || tag[i] == '\''))
                {
                    var quote = tag[i];
                    i++;
                    var valueStart = i;
                    while (i < tag.Length && tag[i] != quote) i++;
                    value = htmlValue(tag[valueStart..Math.Min(i, tag.Length)]);
                    if (i < tag.Length) i++;
                }
                else
                {
                    var valueStart = i;
                    while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '>') i++;
                    value = htmlValue(tag[valueStart..i]);
                }
            }
            if (key.Length > 0) attributes.Add((key.ToLowerInvariant(), value));
        }
        return (name.ToString(), attributes, selfClosing);
    }

    private static string htmlValue(string raw) => DecodeEntities(raw);

    /// <summary>Decode the named and numeric entities the corpus pages use (WebUtility covers them).</summary>
    private static string DecodeEntities(string text)
        => System.Net.WebUtility.HtmlDecode(text);
}