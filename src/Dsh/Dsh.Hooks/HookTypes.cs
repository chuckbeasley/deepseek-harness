using System.Text.Json.Serialization;

namespace Dsh.Hooks;

/// <summary>The bridge dialect that ran a hook.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HookDialect
{
    /// <summary>The Claude Code bridge.</summary>
    [JsonStringEnumMemberName("claude-code")] ClaudeCode,
    /// <summary>The Codex bridge.</summary>
    [JsonStringEnumMemberName("codex")] Codex,
}

/// <summary>How a matcher pattern is interpreted.</summary>
public enum MatcherMode
{
    ClaudeCode,
    Codex,
}

/// <summary>One configured command hook (the shared { type: 'command', command, timeout? } shape).</summary>
public sealed record CommandHook(string Command, int? TimeoutSec = null);

/// <summary>One matcher group: a matcher pattern plus the hooks it runs.</summary>
public sealed record MatcherGroup(string? Matcher, IReadOnlyList<CommandHook> Hooks);

/// <summary>The dialect-neutral outcome a hook produced.</summary>
public sealed record HookOutput
{
    /// <summary>The raw process exit code (null when the hook could not be run).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Trimmed stderr — the block-reason source on a blocking (exit 2) hook.</summary>
    public string Stderr { get; init; } = string.Empty;

    /// <summary>Trimmed stdout, verbatim.</summary>
    public string Stdout { get; init; } = string.Empty;

    /// <summary>False ⇒ the hook asked to halt; true/absent ⇒ proceed.</summary>
    public bool? Continue { get; init; }

    /// <summary>Human-readable reason shown when Continue is false.</summary>
    public string? StopReason { get; init; }

    /// <summary>The neutral blocking decision, when the hook expressed one.</summary>
    public string? Decision { get; init; }

    /// <summary>The reason accompanying the decision.</summary>
    public string? Reason { get; init; }

    /// <summary>The event discriminator claimed by hookSpecificOutput.</summary>
    public string? HookEventName { get; init; }

    /// <summary>Extra context to inject for the next model request.</summary>
    public string? AdditionalContext { get; init; }

    /// <summary>A warning surfaced to the user.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>A tool-input rewrite a hook requested (parsed, not honored — see the TS note).</summary>
    public Dictionary<string, object?>? UpdatedInput { get; init; }
}

/// <summary>
/// Log-only hook invocation record (port of the TS hook/invoked session event; registered into the
/// session event-type registry so the JSONL backend round-trips it).
/// </summary>
public sealed record HookInvokedEvent : Dsh.Session.SessionEvent
{
    public const string EventTypeName = "hook/invoked";

    public required long Turn { get; init; }

    public required string Point { get; init; }

    public required HookDialect Dialect { get; init; }

    public string? Matcher { get; init; }

    public required string HandlerId { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Log-only hook outcome record (port of the TS hook/result session event).</summary>
public sealed record HookResultEvent : Dsh.Session.SessionEvent
{
    public const string EventTypeName = "hook/result";

    public required long Turn { get; init; }

    public required string Point { get; init; }

    public required string HandlerId { get; init; }

    public required string Decision { get; init; }

    public int? ExitCode { get; init; }

    public string? StderrSummary { get; init; }

    public required long DurationMs { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Registers the hook event types with the session event-type registry (bridge boot).</summary>
public static class HookEvents
{
    private static int _registered;

    /// <summary>Register both hook event types once.</summary>
    public static void RegisterEventTypes()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        Dsh.Session.SessionEventTypes.Register(HookInvokedEvent.EventTypeName, typeof(HookInvokedEvent));
        Dsh.Session.SessionEventTypes.Register(HookResultEvent.EventTypeName, typeof(HookResultEvent));
    }
}
