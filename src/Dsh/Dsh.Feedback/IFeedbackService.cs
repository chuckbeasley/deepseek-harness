using Dsh.Llm;

namespace Dsh.Feedback;

/// <summary>
/// The message-feedback capability surface (ctx.feedback): per-message feedback as logged state,
/// folded from <c>feedback/write</c> session events. Each message holds the latest write
/// (last-write-wins) in first-creation order; a log with none folds to an empty state.
/// </summary>
public interface IFeedbackService
{
    /// <summary>Read the current feedback state for one session, folded from its log.</summary>
    /// <param name="session">the session whose log is folded.</param>
    /// <returns>one item per message with feedback, in first-creation order.</returns>
    FeedbackState Current(Dsh.Session.Session session);

    /// <summary>Create or replace feedback for one assistant message, appending the durable event.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="messageId">the target assistant-message identity.</param>
    /// <param name="rating">the overall positive or negative judgment.</param>
    /// <param name="note">an optional explanation; must contain a non-whitespace character and fit the configured UTF-8 byte limit.</param>
    /// <returns>the committed item (creation time retained across rewrites).</returns>
    FeedbackItem Put(Dsh.Session.Session session, MessageId messageId, MessageFeedbackRating rating, string? note = null);

    /// <summary>Delete one message's feedback by appending a durable delete event.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="messageId">the message whose feedback should be absent afterwards.</param>
    /// <returns>true when an item existed and was deleted; false when it was already absent (no event appended).</returns>
    bool Delete(Dsh.Session.Session session, MessageId messageId);
}
