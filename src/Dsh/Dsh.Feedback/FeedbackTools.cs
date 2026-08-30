using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Feedback;

/// <summary>
/// Model-facing Consumer of the feedback capability: the <c>message_feedback</c> tool over
/// <see cref="IFeedbackService"/>. Each call records the human's overall judgment (positive or
/// negative) of one assistant message and appends the durable <see cref="FeedbackEvent"/> through
/// the owning session via the service. The TS surface exposes list/put/delete as a Remote service
/// without a model-facing tool; this tool is the C# Consumer addition for the same durable state.
/// </summary>
public static class FeedbackTools
{
    /// <summary>Model-facing description for one activation (pinned literal).</summary>
    public static string Describe()
    {
        const string text =
            "Record per-message feedback: the human's overall judgment (positive or negative) of "
            + "one assistant message in the current session. Use it when the user explicitly "
            + "praises, criticises, or rates a message. The rating replaces any previous feedback "
            + "for the same message; an optional note preserves the user's explanation verbatim.";
        return text;
    }

    /// <summary>The model-facing parameters schema (pinned literal).</summary>
    public const string ParametersSchemaJson =
        "{\"message_id\":{\"type\":\"string\",\"required\":true,\"description\":\"Stable identity of the assistant message inside the owning session.\"},\"rating\":{\"type\":\"string\",\"required\":true,\"enum\":[\"positive\",\"negative\"],\"description\":\"Overall positive or negative judgment.\"},\"note\":{\"type\":\"string\",\"description\":\"Optional explanation, preserved verbatim after validation.\"}}";

    /// <summary>The canonical output schema (pinned literal).</summary>
    public const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"messageId\":{\"type\":\"string\",\"required\":true},\"rating\":{\"type\":\"string\",\"required\":true,\"enum\":[\"positive\",\"negative\"]},\"note\":{\"type\":\"string\"},\"createdAt\":{\"type\":\"integer\",\"required\":true},\"updatedAt\":{\"type\":\"integer\",\"required\":true}}}";

    /// <summary>
    /// Build the message_feedback ToolDefinition over the feedback service. Execute parses the
    /// model arguments, writes through the service (which appends the durable feedback/write
    /// event), and returns the committed item; Render projects the canonical value to the
    /// model-facing text block.
    /// </summary>
    /// <param name="feedback">the mounted feedback service; must be the live <c>feedback</c> instance.</param>
    public static ToolDefinition Definition(IFeedbackService feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        return new ToolDefinition(
            Name: "message_feedback",
            Description: Describe(),
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var session = context.Session
                    ?? throw new ArgumentException("message_feedback requires an owning session");
                var (messageId, rating, note) = Parse(args);
                var item = feedback.Put(session, messageId, rating, note);
                return Task.FromResult(JsonSerializer.SerializeToElement(item));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderText(value)) });
    }

    /// <summary>Parse and validate the model arguments: message id, rating, and optional note.</summary>
    public static (MessageId MessageId, MessageFeedbackRating Rating, string? Note) Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("message_id", out var rawId))
        {
            throw new ArgumentException("message_feedback arguments must carry a \"message_id\" string");
        }
        var id = rawId.GetString() ?? string.Empty;
        if (id.Length == 0)
        {
            throw new ArgumentException("message_feedback message_id must be a non-empty string");
        }
        if (!args.TryGetProperty("rating", out var rawRating))
        {
            throw new ArgumentException("message_feedback arguments must carry a \"rating\" string");
        }
        var rating = (rawRating.GetString() ?? string.Empty) switch
        {
            "positive" => MessageFeedbackRating.Positive,
            "negative" => MessageFeedbackRating.Negative,
            var other => throw new ArgumentException($"invalid message_feedback rating \"{other}\""),
        };
        string? note = null;
        if (args.TryGetProperty("note", out var rawNote))
        {
            note = rawNote.GetString();
        }
        return (new MessageId(id), rating, note);
    }

    private static string RenderText(JsonElement value)
    {
        var messageId = value.GetProperty("messageId").GetString() ?? string.Empty;
        var rating = value.GetProperty("rating").GetString() ?? string.Empty;
        return $"Feedback recorded for message {messageId}: {rating}.";
    }
}
