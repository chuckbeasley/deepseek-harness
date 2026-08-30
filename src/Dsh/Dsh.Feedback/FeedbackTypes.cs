using System.Text.Json.Serialization;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Feedback;

/// <summary>The human's overall judgment of one assistant message. Serialized with the TS wire strings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageFeedbackRating
{
    /// <summary>The message helped.</summary>
    [JsonStringEnumMemberName("positive")] Positive,
    /// <summary>The message did not help.</summary>
    [JsonStringEnumMemberName("negative")] Negative,
}

/// <summary>One current feedback value for one assistant message.</summary>
public sealed record FeedbackItem(
    [property: JsonPropertyName("messageId")] MessageId MessageId,
    [property: JsonPropertyName("rating")] MessageFeedbackRating Rating,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt);

/// <summary>
/// The folded current feedback state: one item per message, in first-creation order, each holding
/// the latest write (last-write-wins per message). Derives from the session log alone, so resume
/// and fork restore it.
/// </summary>
public sealed record FeedbackState(IReadOnlyList<FeedbackItem> Items)
{
    /// <summary>The state of a log with no feedback/write event.</summary>
    public static FeedbackState Empty { get; } = new(Array.Empty<FeedbackItem>());
}

/// <summary>
/// Plugin-merged session event: one per-message feedback write. A write carries the full item
/// snapshot (last write wins per message on replay); a delete carries no item and removes the
/// message's feedback. Registered into the session event-type registry at the feedback service's
/// construction. The TS message-feedback package stores a durable sidecar table with opaque CAS
/// version tokens; the C# port reframes feedback as logged state, so the fold is last-write-wins
/// and the version token is dropped.
/// </summary>
public sealed record FeedbackEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "feedback/write";

    /// <summary>The target assistant message inside the owning session.</summary>
    public required MessageId MessageId { get; init; }

    /// <summary>The committed item; absent for a delete.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FeedbackItem? Item { get; init; }

    public override string Type => EventTypeName;
}
