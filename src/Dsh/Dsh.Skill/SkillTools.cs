using System.Text.Json;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Skill;

/// <summary>The model-facing shape a loaded skill renders to, shared by the tool result and renderers.</summary>
/// <param name="Name">Kebab-case skill name.</param>
/// <param name="Provider">Provider that owns this skill body.</param>
/// <param name="Content">Markdown instruction body.</param>
/// <param name="ResourceBase">Provider-specific base for relative resources, when one exists.</param>
public sealed record SkillContent(string Name, string Provider, string Content, SkillResourceBase? ResourceBase = null);

/// <summary>
/// Model-facing <c>skill</c> loader tool (the Consumer role of the skill capability seam): a
/// catalog/list/get tool whose result renders one canonical <c>&lt;skill_content&gt;</c> block.
/// </summary>
public static class SkillTools
{
    /// <summary>The registered tool name.</summary>
    public const string ToolName = "skill";

    /// <summary>The model-facing tool description (pinned verbatim from the TS tool).</summary>
    public const string ToolDescription = "Load the full instructions for an available skill. Call this with the exact skill name from the session skill catalog before acting on a task that names or clearly matches that skill.";

    private const string ToolRegistryKey = "tools";

    private const string ParametersJson =
        """
        { "name": { "type": "string", "required": true, "description": "The exact skill name from the available skills list." } }
        """;

    private const string OutputSchemaJson =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "name": { "type": "string", "required": true },
            "provider": { "type": "string", "required": true },
            "resourceBase": {
              "oneOf": [
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "kind": { "type": "string", "required": true, "const": "directory" },
                    "path": { "type": "string", "required": true }
                  }
                },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "kind": { "type": "string", "required": true, "const": "url" },
                    "url": { "type": "string", "required": true }
                  }
                },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "kind": { "type": "string", "required": true, "const": "opaque" },
                    "description": { "type": "string", "required": true }
                  }
                }
              ]
            },
            "content": { "type": "string", "required": true }
          }
        }
        """;

    /// <summary>Build the skill tool definition bound to one registry.</summary>
    public static ToolDefinition Definition(SkillRegistry skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return new ToolDefinition(
            ToolName,
            ToolDescription,
            JsonDocument.Parse(ParametersJson).RootElement.Clone(),
            JsonDocument.Parse(OutputSchemaJson).RootElement.Clone(),
            (args, exec) => ExecuteAsync(skills, args, exec.CancellationToken),
            (_, value) => new ContentBlock[] { new TextBlock(RenderSkillContent(ParseResult(value))) });
    }

    /// <summary>
    /// Register the skill tool on the mounted tool registry (ctx.tools). Fails loud when the tool
    /// registry or the skill registry is absent.
    /// </summary>
    /// <returns>The exact effect disposer that unregisters the tool.</returns>
    public static IDisposable Register(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var skills = ctx.Require<SkillRegistry>(SkillRegistry.ServiceKey);
        var tools = ctx.Require<ToolRuntime>(ToolRegistryKey);
        return tools.Register(Definition(skills));
    }

    /// <summary>
    /// Render one loaded skill for the model. The name rides an escaped attribute; the body is
    /// embedded verbatim (skills are trusted local content).
    /// </summary>
    /// <returns>The complete model-facing <c>&lt;skill_content&gt;</c> block.</returns>
    public static string RenderSkillContent(SkillContent skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var lines = new List<string>
        {
            $"<skill_content name=\"{EscapeAttr(skill.Name)}\">",
            "<skill_resources>",
        };
        lines.AddRange(RenderResourceHint(skill));
        lines.Add("</skill_resources>");
        lines.Add(string.Empty);
        lines.Add("<skill_instructions>");
        lines.Add(skill.Content);
        lines.Add("</skill_instructions>");
        lines.Add("</skill_content>");
        return string.Join("\n", lines);
    }

    /// <summary>Escape model-facing prose embedded inside skill markup so provider-supplied text cannot open or close framing tags.</summary>
    public static string EscapeText(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string EscapeAttr(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;");

    private static IReadOnlyList<string> RenderResourceHint(SkillContent skill)
    {
        switch (skill.ResourceBase)
        {
            case null:
                return new[]
                {
                    $"Resources for this skill are managed by provider \"{EscapeText(skill.Provider)}\".",
                    "Load referenced resources only as needed.",
                };
            case SkillResourceDirectory directory:
                return new[]
                {
                    $"Base directory for this skill: {EscapeText(directory.Path)}",
                    "Resolve relative paths mentioned by this skill against the base directory before using them. Load referenced resources only as needed.",
                };
            case SkillResourceUrl url:
                return new[]
                {
                    $"Base URL for this skill: {EscapeText(url.Url)}",
                    "Resolve relative URLs mentioned by this skill against the base URL before using them. Load referenced resources only as needed.",
                };
            case SkillResourceOpaque opaque:
                return new[]
                {
                    $"Resources for this skill: {EscapeText(opaque.Description)}",
                    "Load referenced resources only as needed.",
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(skill));
        }
    }

    private static async Task<JsonElement> ExecuteAsync(SkillRegistry skills, JsonElement args, CancellationToken cancellationToken)
    {
        var name = args.TryGetProperty("name", out var property) ? property.GetString() : null;
        if (name is null || !SkillNames.IsSkillName(name))
        {
            throw new InvalidOperationException($"invalid skill name \"{name}\"");
        }
        var lookup = new SkillLookupOptions(CancellationToken: cancellationToken);
        var summary = (await skills.ListAsync(lookup)).FirstOrDefault(s => s.Name == name);
        if (summary is null)
        {
            throw new InvalidOperationException($"skill \"{name}\" is unknown or no longer available");
        }
        if (!summary.Invocation.ModelInvocable)
        {
            throw new InvalidOperationException($"skill \"{name}\" is not available for model invocation");
        }
        var skill = await skills.GetAsync(name, lookup);
        if (skill is null)
        {
            throw new InvalidOperationException($"skill \"{name}\" is unknown or no longer available");
        }
        if (!skill.Invocation.ModelInvocable)
        {
            throw new InvalidOperationException($"skill \"{name}\" is not available for model invocation");
        }
        var result = new Dictionary<string, object?>
        {
            ["name"] = skill.Name,
            ["provider"] = skill.Provider,
            ["content"] = skill.Content,
        };
        if (skill.ResourceBase is not null) result["resourceBase"] = ToJson(skill.ResourceBase);
        return JsonSerializer.SerializeToElement(result);
    }

    private static Dictionary<string, object?> ToJson(SkillResourceBase resourceBase)
    {
        switch (resourceBase)
        {
            case SkillResourceDirectory directory:
                return new Dictionary<string, object?> { ["kind"] = "directory", ["path"] = directory.Path };
            case SkillResourceUrl url:
                return new Dictionary<string, object?> { ["kind"] = "url", ["url"] = url.Url };
            case SkillResourceOpaque opaque:
                return new Dictionary<string, object?> { ["kind"] = "opaque", ["description"] = opaque.Description };
            default:
                throw new ArgumentOutOfRangeException(nameof(resourceBase));
        }
    }

    private static SkillContent ParseResult(JsonElement value)
    {
        var name = value.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? string.Empty : string.Empty;
        var provider = value.TryGetProperty("provider", out var providerProperty) ? providerProperty.GetString() ?? string.Empty : string.Empty;
        var content = value.TryGetProperty("content", out var contentProperty) ? contentProperty.GetString() ?? string.Empty : string.Empty;
        SkillResourceBase? resourceBase = null;
        if (value.TryGetProperty("resourceBase", out var resourceBaseProperty) && resourceBaseProperty.ValueKind == JsonValueKind.Object)
        {
            var kind = resourceBaseProperty.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() : null;
            resourceBase = kind switch
            {
                "directory" => new SkillResourceDirectory(resourceBaseProperty.TryGetProperty("path", out var pathProperty) ? pathProperty.GetString() ?? string.Empty : string.Empty),
                "url" => new SkillResourceUrl(resourceBaseProperty.TryGetProperty("url", out var urlProperty) ? urlProperty.GetString() ?? string.Empty : string.Empty),
                "opaque" => new SkillResourceOpaque(resourceBaseProperty.TryGetProperty("description", out var descriptionProperty) ? descriptionProperty.GetString() ?? string.Empty : string.Empty),
                _ => null,
            };
        }
        return new SkillContent(name, provider, content, resourceBase);
    }
}
