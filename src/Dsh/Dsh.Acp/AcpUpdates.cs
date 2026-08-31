using System.Text.Json;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Acp;

/// <summary>
/// Standard ACP updates derived from committed DSH session events (port of the TS
/// <c>updates.ts</c>). The usage update is a documented reduction: the port has no token meter,
/// so <c>usage_update</c> is never emitted.
/// </summary>
public static class AcpUpdates
{
    /// <summary>Convert one committed assistant message into ordered standard update projections.</summary>
    /// <param name="evt">the committed assistant message event.</param>
    /// <returns>the ordered thought and message chunk updates.</returns>
    public static IReadOnlyList<SessionUpdate> AssistantUpdates(AssistantMessageEvent evt)
    {
        var updates = new List<SessionUpdate>();
        foreach (var block in evt.Message.Content)
        {
            switch (block)
            {
                case ReasoningBlock reasoning when reasoning.Text.Length > 0:
                    updates.Add(new AgentThoughtChunkUpdate(evt.Message.Id.Value,
                        JsonSerializer.SerializeToElement(new { type = "text", text = reasoning.Text }, AcpWire.Json)));
                    break;
                case TextBlock text:
                    updates.Add(new AgentMessageChunkUpdate(evt.Message.Id.Value,
                        JsonSerializer.SerializeToElement(new { type = "text", text = text.Text }, AcpWire.Json)));
                    break;
            }
        }
        return updates;
    }

    /// <summary>Start one generic ACP tool lifecycle from the durable call fact.</summary>
    /// <param name="evt">the committed DSH tool-call event.</param>
    /// <returns>the standard generic tool-call update.</returns>
    public static SessionUpdate ToolCallUpdate(ToolCallEvent evt)
        => new Dsh.Acp.ToolCallUpdate(evt.CallId.Value, evt.Name, "other", "in_progress", ParseToolArguments(evt.Arguments));

    /// <summary>Finish one generic ACP tool lifecycle from its committed model-facing result.</summary>
    /// <param name="evt">the committed DSH tool-result event.</param>
    /// <returns>the standard completed or failed tool-call update.</returns>
    public static SessionUpdate ToolResultUpdate(ToolResultEvent evt)
    {
        var result = evt.Message.Result;
        var content = new List<JsonElement>();
        foreach (var block in result.Content)
        {
            if (AcpContent.AssistantBlockToAcp(block) is { } converted)
            {
                content.Add(JsonSerializer.SerializeToElement(new { type = "content", content = converted }, AcpWire.Json));
            }
        }
        return new ToolCallUpdateResult(result.ToolCallId.Value, result.IsError ? "failed" : "completed", content);
    }

    /// <summary>Preserve malformed model output as opaque input instead of dropping the call update.</summary>
    private static JsonElement ParseToolArguments(string value)
    {
        try
        {
            return JsonDocument.Parse(value).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value);
        }
    }
}
