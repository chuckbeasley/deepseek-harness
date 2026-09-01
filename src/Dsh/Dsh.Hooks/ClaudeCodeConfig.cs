using System.Text.Json;

namespace Harness.Hooks;

/// <summary>
/// Parse Claude Code's event-to-matcher-group hook format into shared <see cref="MatcherGroup"/>s
/// (port of the TS <c>parseClaudeCodeConfig</c>). Only command hooks run; other hook types are
/// returned as skipped so the bridge can warn. Plugin-root and project-directory substitutions are
/// applied to commands at parse time.
/// </summary>
public static class ClaudeCodeConfig
{
    /// <summary>The seven Claude Code hook points this bridge parses.</summary>
    public static readonly string[] Events =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SubagentStart", "SubagentStop",
    };

    /// <summary>A skipped non-command hook, surfaced so the bridge can warn about it.</summary>
    public sealed record SkippedHook(string Event, string Type);

    /// <summary>The outcome of parsing one config file: the runnable groups + what was skipped.</summary>
    public sealed record ParsedClaudeConfig(
        IReadOnlyDictionary<string, IReadOnlyList<MatcherGroup>> Config,
        IReadOnlyList<SkippedHook> Skipped);

    /// <summary>
    /// Apply <c>${CLAUDE_PLUGIN_ROOT}</c> / <c>${CLAUDE_PROJECT_DIR}</c> substitution to a command
    /// string. A token whose variable is unset stays verbatim.
    /// </summary>
    /// <param name="command">the raw command from config.</param>
    /// <param name="pluginRoot">replaces <c>${CLAUDE_PLUGIN_ROOT}</c> — the plugin's root dir.</param>
    /// <param name="projectDir">replaces <c>${CLAUDE_PROJECT_DIR}</c> — the project root.</param>
    /// <returns>the command with every occurrence of each set token replaced.</returns>
    public static string SubstituteCommand(string command, string? pluginRoot = null, string? projectDir = null)
    {
        var output = command;
        if (pluginRoot is not null) output = output.Replace("${CLAUDE_PLUGIN_ROOT}", pluginRoot);
        if (projectDir is not null) output = output.Replace("${CLAUDE_PROJECT_DIR}", projectDir);
        return output;
    }

    /// <summary>
    /// Parse either a settings <c>hooks</c> value or a bare <c>hooks.json</c> event map. Malformed
    /// entries are ignored rather than failing boot; unsupported events are ignored before their
    /// groups are parsed, non-command hooks are returned in <see cref="ParsedClaudeConfig.Skipped"/>,
    /// and substitutions are applied to every surviving command. Matcher fields on UserPromptSubmit
    /// and Stop are discarded because those events have no matcher subject. A matcher-bearing
    /// supported runnable group with an invalid regex throws, allowing the bridge to reject the
    /// complete config before listener registration.
    /// </summary>
    /// <param name="raw">the parsed JSON config: a settings object with a <c>hooks</c> key, or the bare event map.</param>
    /// <param name="pluginRoot">substitution value for <c>${CLAUDE_PLUGIN_ROOT}</c>.</param>
    /// <param name="projectDir">substitution value for <c>${CLAUDE_PROJECT_DIR}</c>.</param>
    /// <returns>the runnable per-event groups plus the skipped non-command hooks.</returns>
    public static ParsedClaudeConfig Parse(string rawJson, string? pluginRoot = null, string? projectDir = null)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(rawJson).RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"invalid hook config JSON: {error.Message}", error);
        }
        return ParseElement(root, pluginRoot, projectDir);
    }

    /// <inheritdoc cref="Parse"/>
    public static ParsedClaudeConfig ParseElement(JsonElement root, string? pluginRoot = null, string? projectDir = null)
    {
        var config = new Dictionary<string, IReadOnlyList<MatcherGroup>>(StringComparer.Ordinal);
        var skipped = new List<SkippedHook>();
        // Accept either { hooks: { … } } (a settings file) or the bare event map.
        var hooksMap = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("hooks", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : root;
        if (hooksMap.ValueKind != JsonValueKind.Object) return new ParsedClaudeConfig(config, skipped);

        foreach (var eventName in Events)
        {
            if (!hooksMap.TryGetProperty(eventName, out var rawGroups) || rawGroups.ValueKind != JsonValueKind.Array) continue;
            var groups = new List<MatcherGroup>();
            foreach (var rawGroup in rawGroups.EnumerateArray())
            {
                if (rawGroup.ValueKind != JsonValueKind.Object
                    || !rawGroup.TryGetProperty("hooks", out var rawHooks) || rawHooks.ValueKind != JsonValueKind.Array) continue;
                var commands = new List<CommandHook>();
                foreach (var rawHook in rawHooks.EnumerateArray())
                {
                    if (rawHook.ValueKind != JsonValueKind.Object) continue;
                    var type = rawHook.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                        ? typeValue.GetString()!
                        : "command";
                    if (type != "command")
                    {
                        skipped.Add(new SkippedHook(eventName, type));
                        continue;
                    }
                    if (!rawHook.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) continue;
                    var timeout = rawHook.TryGetProperty("timeout", out var timeoutValue) && timeoutValue.ValueKind == JsonValueKind.Number
                        && timeoutValue.TryGetInt32(out var timeoutSeconds)
                            ? timeoutSeconds
                            : (int?)null;
                    commands.Add(new CommandHook(SubstituteCommand(command.GetString()!, pluginRoot, projectDir), timeout));
                }
                if (commands.Count == 0) continue;
                string? matcher = null;
                if (eventName is not ("UserPromptSubmit" or "Stop"))
                {
                    matcher = rawGroup.TryGetProperty("matcher", out var matcherValue) && matcherValue.ValueKind == JsonValueKind.String
                        ? matcherValue.GetString()
                        : null;
                }
                var diagnostic = HookMatcher.MatcherDiagnostic(matcher, MatcherMode.ClaudeCode);
                if (diagnostic is not null)
                {
                    throw new InvalidOperationException($"{diagnostic} on event \"{eventName}\"");
                }
                groups.Add(new MatcherGroup(matcher, commands));
            }
            if (groups.Count > 0) config[eventName] = groups;
        }
        return new ParsedClaudeConfig(config, skipped);
    }
}
