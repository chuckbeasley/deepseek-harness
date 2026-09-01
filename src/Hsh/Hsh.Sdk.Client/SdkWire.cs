using System.Text.Json;
using Harness.Sdk.Protocol;

namespace Harness.Sdk.Client;

/// <summary>
/// The client's wire helpers: outbound prompt normalization and the inbound <c>session.event</c>
/// envelope validation the run loop reads (ports of the TS <c>normalizeInput</c>,
/// <c>validatedSessionEvent</c>, <c>validatedTurnEndReason</c>, <c>isInboxReceipt</c>, and
/// <c>finalResponse</c>). Wire-boundary probes: a malformed runtime surfaces as an
/// <see cref="SdkProtocolError"/>, never as type-invalid data.
/// </summary>
public static class SdkWire
{
    /// <summary>Normalize run input: a string becomes one text block.</summary>
    /// <param name="input">the prompt text.</param>
    /// <returns>the content blocks to send.</returns>
    public static IReadOnlyList<SdkPromptContentBlock> NormalizeInput(string input)
        => new[] { new SdkPromptContentBlock.Block(new Harness.Llm.TextBlock(input)) };

    /// <summary>Extract and validate one wire <c>session.event</c> envelope from the notification params.</summary>
    /// <param name="parameters">the <c>session.event</c> notification params.</param>
    /// <returns>the envelope; the two variants this module reads are validated, other variants pass
    /// through under their envelope shape.</returns>
    public static WireSessionEvent ValidatedSessionEvent(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object
            || !evt.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            throw new SdkProtocolError($"session.event carried no event envelope: {JsonSerializer.Serialize(parameters)}");
        }
        var data = evt.TryGetProperty("data", out var dataValue) && dataValue.ValueKind == JsonValueKind.Object
            ? dataValue
            : JsonSerializer.SerializeToElement(new { });
        var seq = evt.TryGetProperty("seq", out var seqValue) && seqValue.ValueKind == JsonValueKind.Number
            ? seqValue.GetInt64()
            : 0L;
        var timeMs = evt.TryGetProperty("timeMs", out var timeValue) && timeValue.ValueKind == JsonValueKind.Number
            ? timeValue.GetInt64()
            : 0L;
        var typeText = type.GetString()!;
        if (typeText == "assistant/message")
        {
            var message = data.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.Object
                ? messageValue
                : default;
            var content = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var contentValue)
                ? contentValue
                : default;
            if (content.ValueKind != JsonValueKind.Array || !AllContentBlocksTyped(content))
            {
                throw new SdkProtocolError($"assistant/message event carried malformed content: {JsonSerializer.Serialize(evt)}");
            }
        }
        if (typeText == "turn/end")
        {
            if (!data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.Object)
            {
                throw new SdkProtocolError($"turn/end carried no reason envelope: {JsonSerializer.Serialize(evt)}");
            }
            ValidatedTurnEndReason(reason);
        }
        return new WireSessionEvent(typeText, seq, timeMs, data);
    }

    /// <summary>Whether a raw <c>session.event</c> params object is the durable enqueue receipt for <paramref name="messageId"/>.</summary>
    /// <param name="parameters">the <c>session.event</c> notification params.</param>
    /// <param name="messageId">the queued message identity from the prompt response.</param>
    /// <returns><c>true</c> when the event is the receipt. The port's inbox seam logs no
    /// <c>agent/inbox/spliced</c> event (documented deviation): the durable splice is the
    /// <c>user/message</c> event carrying the queued message id.</returns>
    public static bool IsInboxReceipt(JsonElement parameters, string messageId)
    {
        if (!parameters.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object) return false;
        if (!evt.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "user/message") return false;
        if (!evt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
        if (!data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;
        return message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() == messageId;
    }

    /// <summary>Whether the notification's <c>sessionId</c> params field equals <paramref name="sessionId"/>.</summary>
    public static bool SessionMatches(HarnessNotification notification, string sessionId)
        => notification.Params.TryGetProperty("sessionId", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() == sessionId;

    /// <summary>Whether a <c>session.status</c> notification reports the idle state.</summary>
    public static bool IsIdle(HarnessNotification notification)
        => notification.Params.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && status.GetString() == "idle";

    /// <summary>
    /// Extract the concatenated text of the last assistant message.
    /// </summary>
    /// <param name="events">the activity interval's <c>session.event</c> envelopes in wire order.</param>
    /// <returns>the final response text, or <c>''</c> when no assistant message exists.</returns>
    public static string FinalResponse(IReadOnlyList<WireSessionEvent> events)
    {
        for (var index = events.Count - 1; index >= 0; index--)
        {
            var evt = events[index];
            if (evt.Type != "assistant/message") continue;
            var text = new List<string>();
            if (evt.Data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && block.TryGetProperty("text", out var blockText) && blockText.ValueKind == JsonValueKind.String)
                    {
                        text.Add(blockText.GetString() ?? string.Empty);
                    }
                }
            }
            return string.Concat(text);
        }
        return string.Empty;
    }

    /// <summary>Validate the provider-read fields of one wire turn-end reason envelope.</summary>
    private static void ValidatedTurnEndReason(JsonElement reason)
    {
        if (!reason.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            throw new SdkProtocolError($"turn/end carried no reason envelope: {JsonSerializer.Serialize(reason)}");
        }
        if (kind.GetString() != "aborted") return;
        if (!reason.TryGetProperty("cause", out var cause) || cause.ValueKind != JsonValueKind.Object
            || !cause.TryGetProperty("type", out var causeKind) || causeKind.ValueKind != JsonValueKind.String)
        {
            throw new SdkProtocolError($"turn/end carried a malformed aborted reason: {JsonSerializer.Serialize(reason)}");
        }
        switch (causeKind.GetString())
        {
            case "user":
            case "parent":
            case "disposed":
            case "legacy":
                break;
            case "hook":
                if (!cause.TryGetProperty("reason", out var hookReason) || hookReason.ValueKind != JsonValueKind.String)
                {
                    throw new SdkProtocolError($"turn/end carried a malformed hook abort reason: {JsonSerializer.Serialize(reason)}");
                }
                break;
            default:
                throw new SdkProtocolError($"turn/end carried an unknown abort reason: {JsonSerializer.Serialize(reason)}");
        }
    }

    private static bool AllContentBlocksTyped(JsonElement content)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) return false;
            if (!block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        }
        return true;
    }
}
