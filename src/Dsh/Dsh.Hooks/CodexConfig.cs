using System.Text.Json;

namespace Dsh.Hooks;

/// <summary>
/// Parse Codex's five-event hook subset into shared <see cref="MatcherGroup"/>s (port of the TS
/// <c>parseCodexConfig</c>). Only synchronous command hooks run; other types and <c>async: true</c>
/// commands are recorded as skipped. Codex performs no command substitution.
/// </summary>
public static class CodexConfig
{
    /// <summary>The five Codex hook points this bridge supports.</summary>
    public static readonly string[] Events =
    {
        "PreToolUse", "PostToolUse", "SessionStart", "UserPromptSubmit", "Stop",
    };

    /// <summary>A skipped non-command (or async) hook, surfaced so the bridge can warn.</summary>
    public sealed record SkippedHook(string Event, string Reason);

    /// <summary>The outcome of parsing one Codex config file.</summary>
    public sealed record ParsedCodexConfig(
        IReadOnlyDictionary<string, IReadOnlyList<MatcherGroup>> Config,
        IReadOnlyList<SkippedHook> Skipped);

    /// <summary>
    /// Parse a wrapped or bare Codex event map. Unknown events and malformed entries are ignored
    /// rather than failing boot; unsupported or asynchronous hooks are returned in
    /// <see cref="ParsedCodexConfig.Skipped"/>. Matcher fields on UserPromptSubmit and Stop are
    /// discarded because those events have no matcher subject. A matcher-bearing runnable group
    /// with an invalid regex throws, allowing the bridge to reject the complete config before
    /// listener registration.
    /// </summary>
    /// <param name="rawJson">the parsed JSON config: a <c>{ hooks: … }</c> wrapper or the bare event map.</param>
    /// <returns>the runnable per-event groups plus the skipped hooks with their reasons.</returns>
    public static ParsedCodexConfig Parse(string rawJson)
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
        return ParseElement(root);
    }

    /// <inheritdoc cref="Parse"/>
    public static ParsedCodexConfig ParseElement(JsonElement root)
    {
        var config = new Dictionary<string, IReadOnlyList<MatcherGroup>>(StringComparer.Ordinal);
        var skipped = new List<SkippedHook>();
        var hooksMap = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("hooks", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : root;
        if (hooksMap.ValueKind != JsonValueKind.Object) return new ParsedCodexConfig(config, skipped);

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
                        skipped.Add(new SkippedHook(eventName, $"unsupported \"{type}\" hook"));
                        continue;
                    }
                    if (rawHook.TryGetProperty("async", out var asyncValue) && asyncValue.ValueKind == JsonValueKind.True)
                    {
                        skipped.Add(new SkippedHook(eventName, "async hook"));
                        continue;
                    }
                    if (!rawHook.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) continue;
                    // Codex accepts `timeout` or the `timeoutSec` alias.
                    var timeout = ReadTimeout(rawHook, "timeout") ?? ReadTimeout(rawHook, "timeoutSec");
                    commands.Add(new CommandHook(command.GetString()!, timeout));
                }
                if (commands.Count == 0) continue;
                string? matcher = null;
                if (eventName is not ("UserPromptSubmit" or "Stop"))
                {
                    matcher = rawGroup.TryGetProperty("matcher", out var matcherValue) && matcherValue.ValueKind == JsonValueKind.String
                        ? matcherValue.GetString()
                        : null;
                }
                var diagnostic = HookMatcher.MatcherDiagnostic(matcher, MatcherMode.Codex);
                if (diagnostic is not null)
                {
                    throw new InvalidOperationException($"{diagnostic} on event \"{eventName}\"");
                }
                groups.Add(new MatcherGroup(matcher, commands));
            }
            if (groups.Count > 0) config[eventName] = groups;
        }
        return new ParsedCodexConfig(config, skipped);
    }

    private static int? ReadTimeout(JsonElement hook, string key)
        => hook.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var seconds)
                ? seconds
                : null;
}
