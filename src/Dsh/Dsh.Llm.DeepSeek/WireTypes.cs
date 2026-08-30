using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm.DeepSeek;

/// <summary>Request body for <c>POST {baseUrl}/chat/completions</c> (OpenAI-compatible wire).</summary>
internal sealed record WireRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<WireMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("stream_options")] WireStreamOptions StreamOptions,
    [property: JsonPropertyName("thinking")] WireThinking? Thinking = null,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null,
    [property: JsonPropertyName("tools")] List<WireTool>? Tools = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null);

/// <summary>Thinking-mode toggle (top level, NOT inside extra_body on the wire).</summary>
internal sealed record WireThinking([property: JsonPropertyName("type")] string Type);

/// <summary>Requests usage in every streamed chunk.</summary>
internal sealed record WireStreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

/// <summary>One entry of the request <c>messages</c> array, discriminated on <c>role</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(WireSystemMessage), "system")]
[JsonDerivedType(typeof(WireUserMessage), "user")]
[JsonDerivedType(typeof(WireAssistantMessage), "assistant")]
[JsonDerivedType(typeof(WireToolMessage), "tool")]
internal abstract record WireMessage;

/// <summary>System-role message: a single string of instructions.</summary>
internal sealed record WireSystemMessage([property: JsonPropertyName("content")] string Content) : WireMessage;

/// <summary>User-role message: text-only string content.</summary>
internal sealed record WireUserMessage([property: JsonPropertyName("content")] string Content) : WireMessage;

/// <summary>
/// Assistant-role history message. The harness replays <c>content: ""</c> (never null) on
/// tool-call-only turns; reasoning_content is the CoT passback and tool_calls replay completed calls.
/// </summary>
internal sealed record WireAssistantMessage(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null,
    [property: JsonPropertyName("tool_calls")] List<WireToolCall>? ToolCalls = null) : WireMessage;

/// <summary>Tool-role message: the result of one tool call, keyed by its call id.</summary>
internal sealed record WireToolMessage(
    [property: JsonPropertyName("tool_call_id")] string ToolCallId,
    [property: JsonPropertyName("content")] string Content) : WireMessage;

/// <summary>A completed tool call replayed on an assistant history message; arguments is raw JSON.</summary>
internal sealed record WireToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] WireToolCallFunction Function);

/// <summary>The function half of a completed tool call.</summary>
internal sealed record WireToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>One entry of the request <c>tools</c> array; parameters is a JSON Schema object.</summary>
internal sealed record WireTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] WireToolFunction Function);

/// <summary>The function half of a declared tool.</summary>
internal sealed record WireToolFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

/// <summary>One parsed SSE <c>data:</c> payload (a chat.completion.chunk).</summary>
internal sealed record WireChunk(
    [property: JsonPropertyName("choices")] List<WireChoice>? Choices = null,
    [property: JsonPropertyName("usage")] WireUsage? Usage = null);

/// <summary>One streamed choice; finish_reason is non-null only on its terminal chunk.</summary>
internal sealed record WireChoice(
    [property: JsonPropertyName("delta")] WireDelta? Delta = null,
    [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

/// <summary>The incremental content of one streamed choice; any subset of fields may be present.</summary>
internal sealed record WireDelta(
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null,
    [property: JsonPropertyName("tool_calls")] List<WireToolCallDelta>? ToolCalls = null);

/// <summary>A streamed fragment of one tool call; fragments sharing an index concatenate.</summary>
internal sealed record WireToolCallDelta(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("function")] WireToolCallFunctionDelta? Function = null);

/// <summary>The function half of a streamed tool-call fragment.</summary>
internal sealed record WireToolCallFunctionDelta(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("arguments")] string? Arguments = null);

/// <summary>
/// Wire token accounting. <c>prompt_tokens</c> INCLUDES cache hits; map usage subtracts them to
/// keep the harness convention of disjoint counts.
/// </summary>
internal sealed record WireUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens = null,
    [property: JsonPropertyName("prompt_cache_hit_tokens")] int? PromptCacheHitTokens = null,
    [property: JsonPropertyName("prompt_cache_miss_tokens")] int? PromptCacheMissTokens = null,
    [property: JsonPropertyName("prompt_tokens_details")] WirePromptTokensDetails? PromptTokensDetails = null,
    [property: JsonPropertyName("completion_tokens_details")] WireCompletionTokensDetails? CompletionTokensDetails = null);

/// <summary>OpenAI-compat spelling of the cache-hit count.</summary>
internal sealed record WirePromptTokensDetails(
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens = null);

/// <summary>Per-completion breakdown; reasoning_tokens counts the CoT channel.</summary>
internal sealed record WireCompletionTokensDetails(
    [property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens = null);

/// <summary>Non-2xx error body.</summary>
internal sealed record WireError([property: JsonPropertyName("error")] WireErrorBody? Error = null);

/// <summary>Provider error facts; any subset of fields may be present.</summary>
internal sealed record WireErrorBody(
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("code")] string? Code = null);
