using System.Runtime.CompilerServices;
using System.Text;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Session;

namespace Harness.Feedback;

/// <summary>
/// ctx.feedback: the message-feedback service. It registers the plugin-merged
/// <see cref="FeedbackEvent"/> in the session event-type registry (so the JSONL backend can
/// round-trip it) and folds the session log into current feedback state, last-write-wins per
/// message in first-creation order. The fold subscribes to <c>session/event</c> once and advances
/// each session's cell eagerly; a session predating the service folds its committed log on first
/// read, so resume and fork restore the state. Writes validate the optional note (non-blank,
/// within the configured complete UTF-8 byte bound) and append the durable event.
/// The <c>Session</c> type is fully qualified because the <c>Dsh</c> root namespace member
/// <c>Session</c> (the Harness.Session namespace) shadows the imported type at simple-name lookup.
/// </summary>
public sealed class SessionFeedbackService : Service, IFeedbackService
{
    private const int DefaultMaxNoteBytes = 4096;

    private readonly int _maxNoteBytes;
    private readonly ConditionalWeakTable<Harness.Session.Session, Cell> _cells = new();

    /// <summary>Create and install the service as <c>feedback</c>.</summary>
    /// <param name="ctx">the owner context whose <c>session/event</c> stream is observed.</param>
    /// <param name="maxNoteBytes">the maximum UTF-8 byte length accepted for one note.</param>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="maxNoteBytes"/> is not a positive integer.</exception>
    public SessionFeedbackService(Context ctx, int maxNoteBytes = DefaultMaxNoteBytes)
        : base(ctx, "feedback")
    {
        if (maxNoteBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNoteBytes), "maxNoteBytes must be a positive integer");
        }
        _maxNoteBytes = maxNoteBytes;
        // Plugin-boot equivalent of the TS event-type registration: the JSONL backend must
        // serialize and replay this plugin-merged event.
        SessionEventTypes.Register(FeedbackEvent.EventTypeName, typeof(FeedbackEvent));
        ctx.On("session/event", (Delegate)(Action<Harness.Session.Session, SessionEvent>)Drive);
    }

    /// <summary>Read the feedback service from a context, failing explicitly when it is absent.</summary>
    public static SessionFeedbackService Require(Context ctx) => ctx.Require<SessionFeedbackService>("feedback");

    /// <inheritdoc />
    public FeedbackState Current(Harness.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _cells.GetValue(session, BuildCell).State;
    }

    /// <inheritdoc />
    public FeedbackItem Put(Harness.Session.Session session, MessageId messageId, MessageFeedbackRating rating, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var resolvedNote = ResolveNote(note);
        var state = Current(session);
        var existing = state.Items.FirstOrDefault(item => item.MessageId == messageId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = new FeedbackItem(
            messageId,
            rating,
            resolvedNote,
            existing?.CreatedAt ?? now,
            existing is null ? now : Math.Max(now, existing.UpdatedAt));
        session.Append(new FeedbackEvent { MessageId = messageId, Item = item });
        return item;
    }

    /// <inheritdoc />
    public bool Delete(Harness.Session.Session session, MessageId messageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = Current(session);
        if (state.Items.All(item => item.MessageId != messageId)) return false;
        session.Append(new FeedbackEvent { MessageId = messageId, Item = null });
        return true;
    }

    /// <summary>Eager drive: fold one committed <c>feedback/write</c> into its session's cell.</summary>
    private void Drive(Harness.Session.Session session, SessionEvent evt)
    {
        if (evt is not FeedbackEvent write) return;
        var cell = _cells.GetValue(session, BuildCell);
        if (cell.ObservedSeq >= evt.Seq) return;
        cell.ObservedSeq = evt.Seq;
        cell.State = ApplyWrite(cell.State, write);
    }

    /// <summary>Fold one session's committed log into the current feedback state (last write wins per message).</summary>
    private static Cell BuildCell(Harness.Session.Session session)
    {
        var state = FeedbackState.Empty;
        long observed = -1;
        foreach (var evt in session.Events)
        {
            observed = evt.Seq;
            if (evt is FeedbackEvent write) state = ApplyWrite(state, write);
        }
        return new Cell { State = state, ObservedSeq = observed };
    }

    /// <summary>Apply one decoded write: upsert in creation order, or remove on a delete.</summary>
    private static FeedbackState ApplyWrite(FeedbackState state, FeedbackEvent write)
    {
        if (write.Item is null)
        {
            return new FeedbackState(state.Items.Where(item => item.MessageId != write.MessageId).ToArray());
        }
        var items = state.Items.ToList();
        var index = items.FindIndex(item => item.MessageId == write.MessageId);
        if (index < 0) items.Add(write.Item);
        else items[index] = write.Item;
        return new FeedbackState(items);
    }

    /// <summary>Validate optional-note semantics and the configured complete UTF-8 byte bound.</summary>
    private string? ResolveNote(string? note)
    {
        if (note is null) return null;
        if (note.Trim().Length == 0)
        {
            throw new FeedbackError("note must contain a non-whitespace character", FeedbackErrorCode.NoteBlank);
        }
        var bytes = Encoding.UTF8.GetByteCount(note);
        if (bytes > _maxNoteBytes)
        {
            throw new FeedbackError($"note exceeds the {_maxNoteBytes} UTF-8 byte limit", FeedbackErrorCode.NoteTooLarge);
        }
        return note;
    }

    /// <summary>One session's folded cell: the current state and the seq of the last folded event.</summary>
    private sealed class Cell
    {
        public FeedbackState State { get; set; } = FeedbackState.Empty;

        public long ObservedSeq { get; set; } = -1;
    }
}
