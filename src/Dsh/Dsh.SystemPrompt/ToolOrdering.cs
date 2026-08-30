using Dsh.Llm;

namespace Dsh.SystemPrompt;

/// <summary>Configured tool-order validation and application (TS orderTools semantics).</summary>
public static class ToolOrdering
{
    /// <summary>
    /// Validate a configured toolOrder: no duplicates and exactly one
    /// <see cref="PromptConstants.ToolOrderRest"/> entry. Shape violations fail at service
    /// construction; unknown names fail at assembly because plugins have not loaded yet.
    /// </summary>
    public static string[]? Validate(string[]? toolOrder)
    {
        if (toolOrder is null) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in toolOrder)
        {
            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"toolOrder lists \"{name}\" more than once");
            }
        }
        if (!seen.Contains(PromptConstants.ToolOrderRest))
        {
            throw new InvalidOperationException(
                $"toolOrder must contain the \"{PromptConstants.ToolOrderRest}\" rest entry (where unlisted tools are inserted)");
        }
        return toolOrder;
    }

    /// <summary>
    /// Apply the configured tool order, inserting unlisted tools lexicographically at the rest
    /// entry. Without a configured order, tools sort lexicographically by name. Unknown configured
    /// names fail; known but hidden names may be absent.
    /// </summary>
    public static IReadOnlyList<ToolSchema> Order(IReadOnlyList<ToolSchema> tools, string[]? toolOrder, IReadOnlySet<string> knownNames)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var reserved = tools.FirstOrDefault(tool => tool.Name == PromptConstants.ToolOrderRest);
        if (reserved is not null)
        {
            throw new InvalidOperationException(
                $"tool provider returned reserved tool name \"{PromptConstants.ToolOrderRest}\" (reserved for toolOrder's rest entry)");
        }
        if (toolOrder is null)
        {
            return tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        }
        var unknown = toolOrder
            .Where(name => name != PromptConstants.ToolOrderRest && !knownNames.Contains(name))
            .ToArray();
        if (unknown.Length > 0)
        {
            var listed = string.Join(", ", unknown.Select(name => $"\"{name}\""));
            var known = knownNames.Count == 0
                ? "(none)"
                : string.Join(", ", knownNames.OrderBy(name => name, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"toolOrder lists unregistered tool{(unknown.Length > 1 ? "s" : "")} {listed}; known tools: {known}");
        }
        var listedNames = new HashSet<string>(toolOrder, StringComparer.Ordinal);
        var rest = tools
            .Where(tool => !listedNames.Contains(tool.Name))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
        var result = new List<ToolSchema>();
        foreach (var name in toolOrder)
        {
            if (name == PromptConstants.ToolOrderRest)
            {
                result.AddRange(rest);
            }
            else
            {
                result.AddRange(tools.Where(tool => tool.Name == name));
            }
        }
        return result;
    }
}
