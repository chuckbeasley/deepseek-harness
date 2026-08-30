namespace Dsh.Llm.DeepSeek;

/// <summary>
/// Serialize harness requests into the DeepSeek chat-completions wire format (text-only; the seam
/// has no image blocks yet). Always streaming with usage reporting on; optional fields are omitted
/// rather than sent as null so provider defaults apply.
/// </summary>
internal static class RequestSerializer
{
    /// <summary>Build the full wire request from the harness options and connection defaults.</summary>
    public static WireRequest Serialize(GenerateOptions options, DeepSeekConfig config)
    {
        ArgumentNullException.ThrowIfNull(options);
        var messages = new List<WireMessage>();
        if (options.System is not null) messages.Add(new WireSystemMessage(options.System));
        messages.AddRange(SerializeMessages(options.Messages));

        var tools = options.Tools is { Count: > 0 } toolList
            ? toolList.Select(tool => new WireTool("function", new WireToolFunction(tool.Name, tool.Description, tool.Parameters))).ToList()
            : null;

        var (thinking, reasoningEffort) = ResolveThinking(config);
        return new WireRequest(
            options.Model,
            messages,
            Stream: true,
            StreamOptions: new WireStreamOptions(true),
            Thinking: thinking is null ? null : new WireThinking(thinking),
            ReasoningEffort: reasoningEffort,
            Tools: tools,
            Temperature: options.Temperature,
            MaxTokens: options.MaxTokens);
    }

    /// <summary>
    /// Serialize the conversation. Assistant messages replay content <c>""</c> (never null),
    /// reasoning as reasoning_content, and tool calls verbatim; user-role tool results become
    /// standalone <c>{role: "tool"}</c> messages with <c>"(no output)"</c> when empty.
    /// </summary>
    public static List<WireMessage> SerializeMessages(IReadOnlyList<Message> messages)
    {
        var wire = new List<WireMessage>();
        foreach (var message in messages)
        {
            if (message.Role == "assistant")
            {
                wire.Add(SerializeAssistant(message));
                continue;
            }
            // user role: tool results ride in user messages in the harness vocabulary, but the
            // DeepSeek wire wants them as role:"tool" messages.
            var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
            var text = FlattenText(message.Content);
            if (text.Length > 0 || toolResults.Count == 0)
            {
                wire.Add(new WireUserMessage(text));
            }
            foreach (var result in toolResults)
            {
                var resultText = FlattenText(result.Content);
                wire.Add(new WireToolMessage(result.ToolCallId.Value, resultText.Length > 0 ? resultText : "(no output)"));
            }
        }
        return wire;
    }

    /// <summary>Serialize one assistant message (text + reasoning + tool calls).</summary>
    private static WireMessage SerializeAssistant(Message message)
    {
        var text = FlattenText(message.Content);
        var reasoning = string.Concat(message.Content.OfType<ReasoningBlock>().Select(block => block.Text));
        var toolCalls = message.Content.OfType<ToolCallBlock>()
            .Select(block => new WireToolCall(block.Id.Value, "function", new WireToolCallFunction(block.Name, block.Arguments)))
            .ToList();
        return new WireAssistantMessage(
            text,
            reasoning.Length > 0 ? reasoning : null,
            toolCalls.Count > 0 ? toolCalls : null);
    }

    /// <summary>Join the text blocks of a message.</summary>
    private static string FlattenText(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(block => block.Text));

    /// <summary>Resolve one legal thinking/effort pair without exposing <c>off</c> as a wire effort.</summary>
    private static (string? Thinking, string? ReasoningEffort) ResolveThinking(DeepSeekConfig config)
    {
        var effort = config.ReasoningEffort;
        if (effort == DeepSeekReasoningEffort.Off) return ("disabled", null);
        if (effort is DeepSeekReasoningEffort.Low or DeepSeekReasoningEffort.High or DeepSeekReasoningEffort.Max)
        {
            return ("enabled", effort.Value.ToString().ToLowerInvariant());
        }
        if (config.Thinking is null) return (null, null);
        return (config.Thinking == true ? "enabled" : "disabled", null);
    }
}
