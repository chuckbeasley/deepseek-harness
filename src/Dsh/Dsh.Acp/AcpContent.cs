using System.Text.Json;
using Dsh.Llm;
using Dsh.Sdk.Protocol;

namespace Dsh.Acp;

/// <summary>
/// ACP wire-content admission and projection owned by the ACP adapter (reduced port of the TS
/// <c>content.ts</c>): text blocks are admitted and projected; inline images are refused until
/// the attachment seam admits base64 (the same documented reduction as the SDK server), and
/// unknown block types are refused.
/// </summary>
public static class AcpContent
{
    /// <summary>Detail naming the image admission reduction.</summary>
    public const string ImagePromptReduction = "image prompts await base64 attachment admission (not ported)";

    /// <summary>Admit one wire prompt block array into durable content, in wire order.</summary>
    /// <param name="prompt">the raw ACP prompt array.</param>
    /// <returns>the durable content blocks.</returns>
    public static IReadOnlyList<ContentBlock> AdmitPrompt(JsonElement prompt)
    {
        if (prompt.ValueKind != JsonValueKind.Array)
        {
            throw new JsonRpcResponseError(-32602, "prompt must be an array of content blocks");
        }
        var content = new List<ContentBlock>();
        foreach (var block in prompt.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                throw new JsonRpcResponseError(-32602, "prompt blocks must carry a type");
            }
            switch (type.GetString())
            {
                case "text":
                    if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonRpcResponseError(-32602, "text prompt blocks must carry a text string");
                    }
                    content.Add(new TextBlock(text.GetString()!));
                    break;
                case "image":
                    throw new JsonRpcResponseError(-32602, ImagePromptReduction);
                default:
                    throw new JsonRpcResponseError(-32602, $"prompt block type \"{type.GetString()}\" is not supported");
            }
        }
        return content;
    }

    /// <summary>Project one durable content block to ACP wire content, or <c>null</c> when the block has no ACP projection.</summary>
    /// <param name="block">the durable content block.</param>
    /// <returns>the wire content object, or <c>null</c>.</returns>
    public static JsonElement? AssistantBlockToAcp(ContentBlock block) => block switch
    {
        TextBlock text => JsonSerializer.SerializeToElement(new { type = "text", text = text.Text }, AcpWire.Json),
        _ => null,
    };
}
