using System.Text.Json;

namespace Harness.Llm.DeepSeek;

/// <summary>One open block under assembly inside <see cref="Translate"/>.</summary>
internal sealed class OpenBlock
{
    public int Index { get; init; }

    public required string Kind { get; init; }

    public string Text { get; set; } = string.Empty;

    public string? CallId { get; set; }

    public string? Name { get; set; }
}

/// <summary>
/// Translate DeepSeek SSE payloads into the harness StreamChunk vocabulary with one stateful block
/// per content, reasoning, or tool-call index. An empty initial reasoning delta does not open a
/// block. Finish reason and the latest usage are deferred until <c>[DONE]</c>, covering both
/// finish-attached and trailing usage-only payloads while ensuring no chunk follows <c>finish</c>.
/// </summary>
public static class Translate
{
    /// <summary>
    /// Consume SSE data payloads (ending with <c>[DONE]</c>) and yield StreamChunks. Deltas arrive
    /// as they stream; <c>block-end</c>, <c>usage</c>, and <c>finish</c> are deferred to the
    /// sentinel. Malformed JSON aborts with <c>MALFORMED_RESPONSE</c>; a <c>stop</c> (or absent)
    /// finish with no opened blocks maps to an <c>EMPTY_RESPONSE</c> error finish instead of a
    /// successful empty message.
    /// </summary>
    public static async IAsyncEnumerable<StreamChunk> Run(IAsyncEnumerable<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var nextIndex = 0;
        OpenBlock? textBlock = null;
        OpenBlock? reasoningBlock = null;
        var toolBlocks = new Dictionary<int, OpenBlock>();
        var order = new List<OpenBlock>();
        FinishReason? pendingFinish = null;
        TokenUsage? pendingUsage = null;

        OpenBlock Open(string kind)
        {
            var block = new OpenBlock { Index = nextIndex++, Kind = kind };
            order.Add(block);
            return block;
        }

        await foreach (var payload in payloads)
        {
            if (payload == SseParser.Done)
            {
                foreach (var block in order)
                {
                    yield return new BlockEnd(block.Index, CloseBlock(block));
                }
                if (pendingUsage is not null) yield return new UsageChunk(pendingUsage);
                var reason = pendingFinish ?? new Stop();
                yield return new Finish(reason is Stop && order.Count == 0
                    ? new Error(new LlmFailure("model returned a completed response with no content", "EMPTY_RESPONSE"))
                    : reason);
                yield break;
            }

            WireChunk chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<WireChunk>(payload) ?? throw new JsonException("null payload");
            }
            catch (JsonException)
            {
                var preview = payload.Length > 120 ? payload[..120] : payload;
                throw new LlmError($"malformed SSE payload: {preview}", "MALFORMED_RESPONSE");
            }

            foreach (var choice in chunk.Choices ?? Enumerable.Empty<WireChoice>())
            {
                var delta = choice.Delta;

                // Reasoning first: thinking mode interleaves it before text. The empty-string
                // first chunk must not open a block.
                var reasoning = delta?.ReasoningContent;
                if (!string.IsNullOrEmpty(reasoning))
                {
                    if (reasoningBlock is null)
                    {
                        reasoningBlock = Open("reasoning");
                        yield return new BlockStart(reasoningBlock.Index, "reasoning");
                    }
                    reasoningBlock.Text += reasoning;
                    yield return new ReasoningDelta(reasoningBlock.Index, reasoning);
                }

                var content = delta?.Content;
                if (!string.IsNullOrEmpty(content))
                {
                    if (textBlock is null)
                    {
                        textBlock = Open("text");
                        yield return new BlockStart(textBlock.Index, "text");
                    }
                    textBlock.Text += content;
                    yield return new TextDelta(textBlock.Index, content);
                }

                foreach (var call in delta?.ToolCalls ?? Enumerable.Empty<WireToolCallDelta>())
                {
                    if (!toolBlocks.TryGetValue(call.Index, out var block))
                    {
                        block = Open("tool-call");
                        toolBlocks[call.Index] = block;
                        yield return new BlockStart(block.Index, "tool-call");
                    }
                    if (call.Id is not null) block.CallId = call.Id;
                    if (call.Function?.Name is not null) block.Name = call.Function.Name;
                    var fragment = call.Function?.Arguments ?? string.Empty;
                    block.Text += fragment;
                    yield return new ToolCallDelta(block.Index, new ToolCallId(block.CallId ?? string.Empty), block.Name, fragment);
                }

                if (choice.FinishReason is not null) pendingFinish = MapFinishReason(choice.FinishReason);
            }

            // Usage may arrive attached to the finish chunk or as a trailing usage-only chunk —
            // keep the latest.
            if (chunk.Usage is not null) pendingUsage = MapUsage(chunk.Usage);
        }

        // SseParser guarantees the [DONE] sentinel (or throws); reaching here means the payload
        // source violated that contract.
        throw new LlmError("SSE payload stream ended without [DONE]", "STREAM_CLOSED");
    }

    /// <summary>Map the wire finish_reason vocabulary to the harness FinishReason.</summary>
    internal static FinishReason MapFinishReason(string reason) => reason switch
    {
        "stop" => new Stop(),
        "tool_calls" => new ToolCalls(),
        "length" => new MaxTokens(),
        // content_filter, insufficient_system_resource, future additions.
        _ => new Error(new LlmFailure($"model stopped: {reason}", reason.ToUpperInvariant())),
    };

    /// <summary>
    /// Map wire usage fields. DeepSeek's <c>prompt_tokens</c> INCLUDES cache hits, while the
    /// harness convention is disjoint counts, so cache reads are subtracted out of
    /// <c>inputTokens</c>. An exact total is present only when the aggregate counters are valid and
    /// agree with any wire total.
    /// </summary>
    internal static TokenUsage MapUsage(WireUsage usage)
    {
        var cacheRead = usage.PromptTokensDetails?.CachedTokens ?? usage.PromptCacheHitTokens;
        var reasoning = usage.CompletionTokensDetails?.ReasoningTokens;
        long combined = (long)usage.PromptTokens + usage.CompletionTokens;
        var hasExactTotal = usage.PromptTokens >= 0
            && usage.CompletionTokens >= 0
            && combined <= int.MaxValue
            && (usage.TotalTokens is null || usage.TotalTokens == combined);
        return new TokenUsage(
            usage.PromptTokens - (cacheRead ?? 0),
            usage.CompletionTokens,
            hasExactTotal ? (int)combined : null,
            cacheRead,
            null,
            reasoning);
    }

    /// <summary>Assemble the final ContentBlock for one open block.</summary>
    private static ContentBlock CloseBlock(OpenBlock block) => block.Kind switch
    {
        "text" => new TextBlock(block.Text),
        "reasoning" => new ReasoningBlock(block.Text),
        "tool-call" => new ToolCallBlock(new ToolCallId(block.CallId ?? string.Empty), block.Name ?? string.Empty, block.Text),
        _ => throw new InvalidOperationException($"cannot close unknown block kind \"{block.Kind}\""),
    };
}
