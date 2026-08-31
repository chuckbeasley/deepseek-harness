using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Session;

/// <summary>How a session event entered the ordered surface. Only message-producing events carry one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SurfaceOp
{
    /// <summary>Added to the tail — the normal path for user/assistant/tool messages.</summary>
    [JsonStringEnumMemberName("append")]
    Append,
}

/// <summary>Why a turn ended (merge-extensible in TS; the spike declares the known variants).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CompletedReason), "completed")]
[JsonDerivedType(typeof(AbortedReason), "aborted")]
[JsonDerivedType(typeof(BlockedReason), "blocked")]
[JsonDerivedType(typeof(ErrorReason), "error")]
[JsonDerivedType(typeof(MaxTokensReason), "max-tokens")]
[JsonDerivedType(typeof(InterruptedReason), "interrupted")]
public abstract record TurnEndReason;

/// <summary>The turn completed normally.</summary>
public sealed record CompletedReason : TurnEndReason;

/// <summary>A cancellation request interrupted the live turn.</summary>
public sealed record AbortedReason(TurnEndCancelCause Cause) : TurnEndReason;

/// <summary>The turn was blocked before any step.</summary>
public sealed record BlockedReason : TurnEndReason;

/// <summary>The turn failed with a structured failure.</summary>
public sealed record ErrorReason([property: JsonPropertyName("error")] LlmFailure Failure) : TurnEndReason;

/// <summary>At least one step reached its output-token ceiling.</summary>
public sealed record MaxTokensReason : TurnEndReason;

/// <summary>A persistence backend closed a crash-orphaned turn on reload.</summary>
public sealed record InterruptedReason : TurnEndReason;

/// <summary>Why an active agent driver was cancelled (merge-extensible).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserCancel), "user")]
[JsonDerivedType(typeof(ParentCancel), "parent")]
[JsonDerivedType(typeof(HookCancel), "hook")]
[JsonDerivedType(typeof(DisposedCancel), "disposed")]
[JsonDerivedType(typeof(LegacyCancel), "legacy")]
public abstract record TurnEndCancelCause;

/// <summary>Cancelled by the end user.</summary>
public sealed record UserCancel : TurnEndCancelCause;

/// <summary>Cancelled by a parent agent.</summary>
public sealed record ParentCancel : TurnEndCancelCause;

/// <summary>Cancelled by a hook with an explicit reason.</summary>
public sealed record HookCancel(string Reason) : TurnEndCancelCause;

/// <summary>Cancelled because the lifecycle was disposed.</summary>
public sealed record DisposedCancel : TurnEndCancelCause;

/// <summary>Legacy durable cause from before coarse records carried one.</summary>
public sealed record LegacyCancel : TurnEndCancelCause;

/// <summary>
/// Logged request state outside derived history: call config, system prompt, and tools. The latest
/// full request/header snapshot reconstructs it; canonical empty optional fields are absent.
/// </summary>
public sealed record EpochHeader
{
    /// <summary>The conversation's call configuration (provider, model, and sampling scalars).</summary>
    public required LlmCallConfig Config { get; init; }

    /// <summary>Effective config fields materialized from the exact adapter rather than proposed by a caller.</summary>
    public LlmCallConfigAdapterDefaults? AdapterDefaults { get; init; }

    /// <summary>Rendered system prompt text; absent for a system-less request.</summary>
    public string? System { get; init; }

    /// <summary>Assembled tool schemas; absent for a tool-less request.</summary>
    public IReadOnlyList<ToolSchema>? Tools { get; init; }
}

/// <summary>Why a request/header snapshot was appended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequestHeaderReason
{
    /// <summary>The log's first header (a new conversation).</summary>
    [JsonStringEnumMemberName("initial")]
    Initial,
    /// <summary>A loop instance's first request over a log that already has header events.</summary>
    [JsonStringEnumMemberName("resume")]
    Resume,
    /// <summary>A later request used a different header.</summary>
    [JsonStringEnumMemberName("change")]
    Change,
    /// <summary>An unchanged header began an explicitly distinct message series.</summary>
    [JsonStringEnumMemberName("series")]
    Series,
}

/// <summary>Structured error metadata for a failed tool call (alongside the model-facing text).</summary>
public sealed record ToolErrorInfo(string Name, string Code);

/// <summary>
/// One immutable entry in the session log: the envelope (id/seq/time + type discriminant) and the
/// payload on the same record. The type discriminant is a computed read-only property, so it
/// serializes as "type" but is skipped on read; the polymorphic "$type" discriminator drives
/// round-trips. Records are immutable by construction — there is no update or delete path.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TurnStartEvent), "turn/start")]
[JsonDerivedType(typeof(TurnEndEvent), "turn/end")]
[JsonDerivedType(typeof(StepStartEvent), "step/start")]
[JsonDerivedType(typeof(StepEndEvent), "step/end")]
[JsonDerivedType(typeof(UserMessageEvent), "user/message")]
[JsonDerivedType(typeof(AssistantChunkEvent), "assistant/chunk")]
[JsonDerivedType(typeof(AssistantMessageEvent), "assistant/message")]
[JsonDerivedType(typeof(ToolCallEvent), "tool/call")]
[JsonDerivedType(typeof(ToolResultEvent), "tool/result")]
[JsonDerivedType(typeof(RequestHeaderEvent), "request/header")]
[JsonDerivedType(typeof(RequestContextEvent), "request/context")]
public abstract record SessionEvent
{
    /// <summary>Event identity (NEW versus the TS envelope, which has type/seq/time/data only — see Q1 in spike-design.md).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Monotonic sequence number within the session; always the log length at append.</summary>
    public long Seq { get; init; }

    /// <summary>Unix epoch milliseconds.</summary>
    public long TimeMs { get; init; }

    /// <summary>The event type discriminant (e.g. "user/message").</summary>
    public abstract string Type { get; }
}

/// <summary>Opens turn <see cref="Turn"/> before the loop claims queued input or runs pre-step.</summary>
public sealed record TurnStartEvent : SessionEvent
{
    public required long Turn { get; init; }

    public override string Type => "turn/start";
}

/// <summary>Closes turn <see cref="Turn"/> with the <see cref="Reason"/> that ended it.</summary>
public sealed record TurnEndEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required TurnEndReason Reason { get; init; }

    public override string Type => "turn/end";
}

/// <summary>Opens step <see cref="Step"/> of turn <see cref="Turn"/> — one model call plus its tool executions.</summary>
public sealed record StepStartEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required long Step { get; init; }

    public override string Type => "step/start";
}

/// <summary>Closes step <see cref="Step"/> of turn <see cref="Turn"/>.</summary>
public sealed record StepEndEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required long Step { get; init; }

    public override string Type => "step/end";
}

/// <summary>A user-role message on the model-visible surface.</summary>
public sealed record UserMessageEvent : SessionEvent
{
    public required UserMessage Message { get; init; }

    public required SurfaceOp SurfaceOp { get; init; }

    public IReadOnlyList<long>? SourceEventSeqs { get; init; }

    public override string Type => "user/message";
}

/// <summary>Raw stream chunk — token-level replay fidelity.</summary>
public sealed record AssistantChunkEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required long Step { get; init; }

    public required StreamChunk Chunk { get; init; }

    public override string Type => "assistant/chunk";
}

/// <summary>Assembled assistant message for one step (derived history uses this).</summary>
public sealed record AssistantMessageEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required long Step { get; init; }

    public required AssistantMessage Message { get; init; }

    public TokenUsage? Usage { get; init; }

    /// <summary>Whether the stream was interrupted before a terminal finish (absent when false).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Interrupted { get; init; }

    public required SurfaceOp SurfaceOp { get; init; }

    public IReadOnlyList<long>? SourceEventSeqs { get; init; }

    public override string Type => "assistant/message";
}

/// <summary>The model requested one tool invocation: <see cref="Name"/> with the raw <see cref="Arguments"/> JSON string exactly as the model produced it (unparsed).</summary>
public sealed record ToolCallEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required long Step { get; init; }

    public required ToolCallId CallId { get; init; }

    public required string Name { get; init; }

    public required string Arguments { get; init; }

    public override string Type => "tool/call";
}

/// <summary>A completed tool call's model-facing result.</summary>
public sealed record ToolResultEvent : SessionEvent
{
    public required long Turn { get; init; }

    public required long Step { get; init; }

    public required ToolResultMessage Message { get; init; }

    public ToolErrorInfo? Error { get; init; }

    public JsonElement? Meta { get; init; }

    public required SurfaceOp SurfaceOp { get; init; }

    public IReadOnlyList<long>? SourceEventSeqs { get; init; }

    public override string Type => "tool/result";
}

/// <summary>Full header for the next request, appended inside its step before dispatch.</summary>
public sealed record RequestHeaderEvent : SessionEvent
{
    public required EpochHeader Header { get; init; }

    public required RequestHeaderReason Reason { get; init; }

    /// <summary>Whether this header began an explicitly distinct message series (absent when false).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StartsSeries { get; init; }

    public override string Type => "request/header";
}

/// <summary>Route metadata for the next request, logged only when the route or capacity changes.</summary>
public sealed record RequestContextEvent : SessionEvent
{
    public required string Provider { get; init; }

    public required string Model { get; init; }

    public long? ContextWindow { get; init; }

    public override string Type => "request/context";
}



