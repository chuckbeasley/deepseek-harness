using System.Globalization;
using Harness.Cordis.Core;

namespace Harness.Skill;

/// <summary>File locator carried by one filesystem skill candidate.</summary>
/// <param name="Path">Absolute path of the skill markdown file.</param>
/// <param name="Directory">Absolute directory skills resolve relative resources against.</param>
public sealed record SkillLocator(string Path, string Directory);

/// <summary>
/// Local filesystem skill provider: discovers directory-bundle (SKILL.md) and flat Markdown skills
/// from one root directory, parses YAML frontmatter, and loads bodies on demand. A missing root
/// fails loud at list time (the TS provider silently skips absent roots; the C# port treats a
/// configured-but-absent root as misconfiguration).
/// </summary>
public sealed class FileSystemSkillProvider : ISkillProvider
{
    private readonly string _root;
    private readonly string _providerName;
    private readonly string _source;
    private readonly int _rank;
    private readonly Context? _ctx;

    /// <summary>
    /// Create a provider over one skill root.
    /// </summary>
    /// <param name="root">Absolute or relative root directory scanned for skill folders and flat Markdown files.</param>
    /// <param name="providerName">Unique provider name; defaults to <c>filesystem</c>.</param>
    /// <param name="source">Discovery source advertised on every candidate; defaults to <c>custom</c>.</param>
    /// <param name="rank">Precedence rank for duplicate skill names; defaults to the custom-root rank 300.</param>
    /// <param name="ctx">Optional context whose logger receives file-parse warnings.</param>
    public FileSystemSkillProvider(string root, string? providerName = null, string source = "custom", int rank = 300, Context? ctx = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = Path.GetFullPath(root);
        _providerName = providerName ?? "filesystem";
        _source = source;
        _rank = rank;
        _ctx = ctx;
    }

    /// <inheritdoc/>
    public string Name => _providerName;

    /// <summary>
    /// Discover local skill candidates from the configured root.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">when the configured root directory does not exist.</exception>
    public Task<IReadOnlyList<SkillCandidate>> ListAsync(SkillLookupOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"skill root \"{_root}\" does not exist");
        }
        var skills = new List<SkillCandidate>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root).OrderBy(e => e, StringComparer.Ordinal))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFileName(entry);
            string path;
            string directory;
            if (Directory.Exists(entry))
            {
                path = Path.Combine(entry, "SKILL.md");
                directory = entry;
            }
            else if (entryName.EndsWith(".md", StringComparison.Ordinal))
            {
                path = entry;
                directory = _root;
            }
            else
            {
                continue;
            }
            var parsed = ParseSkillFile(path);
            if (parsed is null) continue;
            skills.Add(new SkillCandidate(
                parsed.Name,
                parsed.Description,
                parsed.Invocation,
                _source,
                _providerName,
                _rank,
                new SkillLocator(path, directory),
                parsed.WhenToUse,
                new SkillResourceDirectory(directory),
                path,
                parsed.Metadata));
        }
        return Task.FromResult<IReadOnlyList<SkillCandidate>>(skills);
    }

    /// <summary>Load a complete local skill body from the candidate's file locator.</summary>
    /// <returns>The full local skill, or <c>null</c> if the file disappeared.</returns>
    public Task<SkillDefinition?> GetAsync(SkillCandidate candidate, SkillLookupOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (candidate.Locator is not SkillLocator locator)
        {
            throw new InvalidOperationException($"skill provider \"{_providerName}\" received a foreign candidate locator");
        }
        var parsed = ParseSkillFile(locator.Path);
        if (parsed is null) return Task.FromResult<SkillDefinition?>(null);
        return Task.FromResult<SkillDefinition?>(new SkillDefinition(
            parsed.Name,
            parsed.Description,
            parsed.Invocation,
            candidate.Source,
            _providerName,
            parsed.Content,
            parsed.WhenToUse,
            new SkillResourceDirectory(locator.Directory),
            locator.Path,
            parsed.Metadata));
    }

    private ParsedSkill? ParseSkillFile(string path)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        var frontmatter = SkillFrontmatter.Parse(raw);
        if (frontmatter is null)
        {
            Warn($"skill file {path} ignored: missing YAML frontmatter");
            return null;
        }
        var data = frontmatter.Data;
        var name = StringField(data, "name");
        var description = StringField(data, "description");
        if (name is null || description is null)
        {
            Warn($"skill file {path} ignored: frontmatter requires name and description");
            return null;
        }
        if (!SkillNames.IsSkillName(name))
        {
            Warn($"skill file {path} ignored: invalid skill name \"{name}\"");
            return null;
        }
        SkillInvocationPolicy invocation;
        try
        {
            invocation = ParseInvocationPolicy(data);
        }
        catch (Exception error)
        {
            Warn($"skill file {path} ignored: invalid invocation frontmatter: {error.Message}");
            return null;
        }
        return new ParsedSkill(name, description, OptionalString(data, "whenToUse"), invocation, OptionalMetadata(data), frontmatter.Body.Trim());
    }

    private static SkillInvocationPolicy ParseInvocationPolicy(Dictionary<string, object?> data)
    {
        RejectLegacyInvocationKey(data, "disableModelInvocation", "disable-model-invocation");
        RejectLegacyInvocationKey(data, "modelInvocable", "disable-model-invocation");
        RejectLegacyInvocationKey(data, "userInvocable", "user-invocable");
        var disableModelInvocation = FrontmatterBoolean(data, "disable-model-invocation");
        var userInvocable = FrontmatterBoolean(data, "user-invocable");
        return new SkillInvocationPolicy(disableModelInvocation != true, userInvocable != false);
    }

    private static void RejectLegacyInvocationKey(Dictionary<string, object?> data, string legacy, string canonical)
    {
        if (data.ContainsKey(legacy))
        {
            throw new ArgumentException($"frontmatter field \"{legacy}\" is unsupported; use \"{canonical}\"");
        }
    }

    private static bool? FrontmatterBoolean(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null) return null;
        if (value is bool boolean) return boolean;
        if (value is long integer)
        {
            if (integer == 1) return true;
            if (integer == 0) return false;
        }
        if (value is string text)
        {
            switch (text.ToLowerInvariant())
            {
                case "true":
                case "yes":
                case "on":
                case "1":
                    return true;
                case "false":
                case "no":
                case "off":
                case "0":
                    return false;
            }
        }
        throw new ArgumentException($"frontmatter field \"{key}\" must be a boolean");
    }

    private static string? StringField(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is not string text || text.Length == 0) return null;
        return text;
    }

    private static string? OptionalString(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is not string text || text.Length == 0) return null;
        return text;
    }

    private static IReadOnlyDictionary<string, object?>? OptionalMetadata(Dictionary<string, object?> data)
    {
        if (!data.TryGetValue("metadata", out var value) || value is not Dictionary<string, object?> map) return null;
        return map;
    }

    private void Warn(string message)
    {
        _ctx?.Logger.Warn(message);
    }

    private sealed record ParsedSkill(
        string Name,
        string Description,
        string? WhenToUse,
        SkillInvocationPolicy Invocation,
        IReadOnlyDictionary<string, object?>? Metadata,
        string Content);
}

/// <summary>
/// Minimal YAML frontmatter parser for skill files: a leading <c>---</c> fence, a mapping of
/// scalar or nested-mapping keys, and a closing <c>---</c> fence. Only the subset skill
/// frontmatter uses is supported; a hand-rolled parser keeps the port free of NuGet dependencies.
/// </summary>
internal static class SkillFrontmatter
{
    /// <summary>Parsed frontmatter: the data mapping and the body text after the closing fence.</summary>
    public sealed record FrontmatterResult(Dictionary<string, object?> Data, string Body);

    /// <summary>Parse the frontmatter block of one skill file; null when absent or unclosed.</summary>
    public static FrontmatterResult? Parse(string text)
    {
        var newline = text.IndexOf('\n');
        var firstLine = newline < 0 ? text : text[..newline];
        if (firstLine.TrimEnd('\r') != "---") return null;
        var start = newline + 1;
        var closing = FindClosing(text, start);
        if (closing is null) return null;
        var data = ParseMapping(text[start..closing.Start]);
        return new FrontmatterResult(data, text[closing.BodyStart..]);
    }

    private static Closing? FindClosing(string text, int start)
    {
        var lineStart = start;
        while (lineStart <= text.Length)
        {
            var next = text.IndexOf('\n', lineStart);
            var lineEnd = next < 0 ? text.Length : next;
            if (text[lineStart..lineEnd].TrimEnd('\r') == "---")
            {
                return new Closing(lineStart, next < 0 ? text.Length : next + 1);
            }
            if (next < 0) return null;
            lineStart = next + 1;
        }
        return null;
    }

    private static Dictionary<string, object?> ParseMapping(string text)
    {
        var result = new Dictionary<string, object?>();
        var lines = text.Split('\n');
        var index = 0;
        var baseIndent = -1;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (IsBlankOrComment(line))
            {
                index += 1;
                continue;
            }
            var indent = IndentOf(line);
            if (baseIndent < 0) baseIndent = indent;
            if (indent < baseIndent) break;
            if (indent > baseIndent)
            {
                // A deeper line without an owning key is malformed; skip it.
                index += 1;
                continue;
            }
            var trimmed = line.TrimEnd('\r').TrimStart();
            var colon = IndexOfColon(trimmed);
            if (colon < 0)
            {
                index += 1;
                continue;
            }
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                index += 1;
                if (index < lines.Length && !IsBlankOrComment(lines[index]) && IndentOf(lines[index]) > indent)
                {
                    result[key] = ParseNestedMapping(lines, ref index, IndentOf(lines[index]));
                }
                else
                {
                    result[key] = new Dictionary<string, object?>();
                }
            }
            else if (value.StartsWith("- ", StringComparison.Ordinal))
            {
                var list = new List<object?> { ParseScalar(value[2..]) };
                index += 1;
                while (index < lines.Length)
                {
                    var nextLine = lines[index];
                    if (IsBlankOrComment(nextLine))
                    {
                        index += 1;
                        continue;
                    }
                    var nextIndent = IndentOf(nextLine);
                    if (nextIndent <= indent) break;
                    var nextTrimmed = nextLine.TrimEnd('\r').TrimStart();
                    if (!nextTrimmed.StartsWith("- ", StringComparison.Ordinal)) break;
                    list.Add(ParseScalar(nextTrimmed[2..]));
                    index += 1;
                }
                result[key] = list;
            }
            else
            {
                result[key] = ParseScalar(value);
                index += 1;
            }
        }
        return result;
    }

    private static Dictionary<string, object?> ParseNestedMapping(string[] lines, ref int index, int baseIndent)
    {
        var result = new Dictionary<string, object?>();
        while (index < lines.Length)
        {
            var line = lines[index];
            if (IsBlankOrComment(line))
            {
                index += 1;
                continue;
            }
            var indent = IndentOf(line);
            if (indent < baseIndent) break;
            if (indent > baseIndent)
            {
                index += 1;
                continue;
            }
            var trimmed = line.TrimEnd('\r').TrimStart();
            var colon = IndexOfColon(trimmed);
            if (colon < 0)
            {
                index += 1;
                continue;
            }
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                index += 1;
                if (index < lines.Length && !IsBlankOrComment(lines[index]) && IndentOf(lines[index]) > indent)
                {
                    result[key] = ParseNestedMapping(lines, ref index, IndentOf(lines[index]));
                }
                else
                {
                    result[key] = new Dictionary<string, object?>();
                }
            }
            else if (value.StartsWith("- ", StringComparison.Ordinal))
            {
                var list = new List<object?> { ParseScalar(value[2..]) };
                index += 1;
                while (index < lines.Length)
                {
                    var nextLine = lines[index];
                    if (IsBlankOrComment(nextLine))
                    {
                        index += 1;
                        continue;
                    }
                    var nextIndent = IndentOf(nextLine);
                    if (nextIndent <= indent) break;
                    var nextTrimmed = nextLine.TrimEnd('\r').TrimStart();
                    if (!nextTrimmed.StartsWith("- ", StringComparison.Ordinal)) break;
                    list.Add(ParseScalar(nextTrimmed[2..]));
                    index += 1;
                }
                result[key] = list;
            }
            else
            {
                result[key] = ParseScalar(value);
                index += 1;
            }
        }
        return result;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal);
    }

    private static int IndentOf(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] is ' ' or '\t') count += 1;
        return count;
    }

    private static int IndexOfColon(string line)
    {
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }
            if (c == ':' && (i + 1 >= line.Length || line[i + 1] is ' ' or '\t')) return i;
        }
        return -1;
    }

    private static object? ParseScalar(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
        {
            return text[1..^1];
        }
        switch (text)
        {
            case "true":
            case "yes":
            case "on":
                return true;
            case "false":
            case "no":
            case "off":
                return false;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return text;
    }

    private sealed record Closing(int Start, int BodyStart);
}
