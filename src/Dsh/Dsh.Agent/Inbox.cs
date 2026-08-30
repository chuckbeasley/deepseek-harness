using Dsh.Llm;

namespace Dsh.Agent;

/// <summary>One of the two ordered pending-message lists owned by an agent (port of InboxTarget).</summary>
public enum InboxTarget
{
    /// <summary>Prompts awaiting individual turns; claim consumes the first queued turn.</summary>
    NextTurn,

    /// <summary>Input awaiting the next step boundary; claim drains it completely.</summary>
    NextStep,
}

/// <summary>Live notifications committed by inbox mutations (port of InboxNotifications).</summary>
public interface IInboxNotifications
{
    /// <summary>Publish one inserted message.</summary>
    void Inserted(UserMessage message);

    /// <summary>Publish one discarded message.</summary>
    void Discarded(UserMessage message);

    /// <summary>Publish one claimed message inside its owning turn.</summary>
    void Claimed(UserMessage message, long turn);
}

/// <summary>
/// The two ordered pending-message lists owned by one agent (port of the TS Inbox projection).
///
/// Deviation from the TS projection: the TS inbox is a replay-once projection that consumes durable
/// <c>agent/inbox/spliced</c> session events and appends a normalized splice per mutation. The C#
/// session vocabulary does not carry that event yet, so this port is live-only: the same splice
/// semantics, identity validation, and notifications operate on in-memory lists, and the durable
/// splice event is deferred until the session event vocabulary gains it.
/// </summary>
public sealed class Inbox
{
    private readonly List<UserMessage> _nextTurn = new();
    private readonly List<UserMessage> _nextStep = new();
    private readonly IInboxNotifications _notifications;
    private readonly int? _maxPending;

    /// <summary>Create the live inbox; <paramref name="notifications"/> receives every committed mutation.</summary>
    public Inbox(IInboxNotifications notifications, int? maxPending = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _maxPending = maxPending;
    }

    /// <summary>Prompts awaiting individual turns.</summary>
    public IReadOnlyList<UserMessage> NextTurn => _nextTurn;

    /// <summary>Input awaiting the next step boundary.</summary>
    public IReadOnlyList<UserMessage> NextStep => _nextStep;

    /// <summary>Whether either pending-message list contains work.</summary>
    public bool HasPending => _nextTurn.Count > 0 || _nextStep.Count > 0;

    /// <summary>
    /// Durably cancel all pending input, clearing next-step before next-turn. (In this live-only
    /// port "durably" reduces to the in-memory lists; see the class note.)
    /// </summary>
    public void Clear()
    {
        Mutate(InboxTarget.NextStep, 0, _nextStep.Count, Array.Empty<UserMessage>(), discardRemoved: true);
        Mutate(InboxTarget.NextTurn, 0, _nextTurn.Count, Array.Empty<UserMessage>(), discardRemoved: true);
    }

    /// <summary>
    /// Remove and return the complete batch proposed for one step, publishing each claimed message.
    /// <paramref name="target"/> selects whether this boundary also consumes one queued turn.
    /// </summary>
    /// <param name="target">whether this boundary also consumes one queued turn.</param>
    /// <param name="turn">the turn that will own the claimed batch.</param>
    /// <returns>next-step input followed by the queued turn, when requested.</returns>
    public IReadOnlyList<UserMessage> Claim(InboxTarget target, long turn)
    {
        var claimed = Mutate(InboxTarget.NextStep, 0, _nextStep.Count, Array.Empty<UserMessage>(), discardRemoved: false);
        if (target == InboxTarget.NextTurn)
        {
            claimed.AddRange(Mutate(InboxTarget.NextTurn, 0, 1, Array.Empty<UserMessage>(), discardRemoved: false));
        }
        foreach (var message in claimed) _notifications.Claimed(message, turn);
        return claimed;
    }

    /// <summary>Append one message to a pending list, publishing it as inserted.</summary>
    /// <exception cref="InvalidOperationException">when the message identity is already pending.</exception>
    public void Append(InboxTarget target, UserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Mutate(target, CountOf(target), 0, new[] { message }, discardRemoved: false);
    }

    /// <summary>Prepend one message to a pending list, publishing it as inserted.</summary>
    /// <exception cref="InvalidOperationException">when the message identity is already pending.</exception>
    public void Prepend(InboxTarget target, UserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Mutate(target, 0, 0, new[] { message }, discardRemoved: false);
    }

    /// <summary>
    /// Replace one pending message in place, possibly changing its identity. A successful
    /// replacement publishes the old message as discarded and the new message as inserted.
    /// </summary>
    /// <param name="messageId">identity of the pending message to replace.</param>
    /// <param name="newMessage">replacement message.</param>
    /// <returns>whether the message was still pending.</returns>
    public bool Replace(MessageId messageId, UserMessage newMessage)
    {
        ArgumentNullException.ThrowIfNull(newMessage);
        var location = Locate(messageId);
        if (location is null) return false;
        Mutate(location.Value.Target, location.Value.Index, 1, new[] { newMessage }, discardRemoved: true);
        return true;
    }

    /// <summary>Remove one pending message, publishing it as discarded.</summary>
    /// <param name="messageId">identity of the pending message to remove.</param>
    /// <returns>whether the message was still pending.</returns>
    public bool Remove(MessageId messageId)
    {
        var location = Locate(messageId);
        if (location is null) return false;
        Mutate(location.Value.Target, location.Value.Index, 1, Array.Empty<UserMessage>(), discardRemoved: true);
        return true;
    }

    /// <summary>
    /// Apply standard splice semantics (negative starts count from the tail) and publish the
    /// normalized result. Removed messages are published as discarded, inserted ones as inserted.
    /// </summary>
    /// <returns>the messages removed by the splice.</returns>
    public IReadOnlyList<UserMessage> Splice(InboxTarget target, int start, int deleteCount, IReadOnlyList<UserMessage> inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        return Mutate(target, start, deleteCount, inserted, discardRemoved: true);
    }

    private List<UserMessage> Mutate(InboxTarget target, int start, int deleteCount, IReadOnlyList<UserMessage> inserted, bool discardRemoved)
    {
        var inbox = ListOf(target);
        var actualStart = start < 0 ? Math.Max(inbox.Count + start, 0) : Math.Min(start, inbox.Count);
        var actualDeleteCount = Math.Min(Math.Max(deleteCount, 0), inbox.Count - actualStart);
        if (actualDeleteCount == 0 && inserted.Count == 0) return new List<UserMessage>();
        Validate(target, actualStart, actualDeleteCount, inserted);
        if (_maxPending is int cap && inbox.Count - actualDeleteCount + inserted.Count > cap)
        {
            throw new InvalidOperationException(
                $"inbox {target} would exceed the configured MaxPendingMessages cap ({cap})");
        }
        var removed = inbox.GetRange(actualStart, actualDeleteCount);
        inbox.RemoveRange(actualStart, actualDeleteCount);
        inbox.InsertRange(actualStart, inserted);
        if (discardRemoved)
        {
            foreach (var message in removed) _notifications.Discarded(message);
        }
        foreach (var message in inserted) _notifications.Inserted(message);
        return removed;
    }

    /// <summary>Validate one normalized splice against the current projection and identity rules.</summary>
    private void Validate(InboxTarget target, int start, int deleteCount, IReadOnlyList<UserMessage> inserted)
    {
        var inbox = ListOf(target);
        if (start < 0 || start > inbox.Count || deleteCount < 0 || start + deleteCount > inbox.Count)
        {
            throw new InvalidOperationException("invalid inbox splice");
        }
        var candidate = new List<UserMessage>(inbox);
        candidate.RemoveRange(start, deleteCount);
        candidate.InsertRange(start, inserted);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in target == InboxTarget.NextTurn
                     ? candidate.Concat(_nextStep)
                     : _nextTurn.Concat(candidate))
        {
            if (!ids.Add(message.Id.Value))
            {
                throw new InvalidOperationException($"message \"{message.Id}\" is already pending");
            }
        }
    }

    private List<UserMessage> ListOf(InboxTarget target) => target switch
    {
        InboxTarget.NextTurn => _nextTurn,
        InboxTarget.NextStep => _nextStep,
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private int CountOf(InboxTarget target) => ListOf(target).Count;

    /// <summary>Locate one pending identity across both owned lists.</summary>
    private (InboxTarget Target, int Index)? Locate(MessageId messageId)
    {
        for (var target = 0; target < 2; target++)
        {
            var inbox = target == 0 ? _nextTurn : _nextStep;
            var index = inbox.FindIndex(message => message.Id == messageId);
            if (index >= 0) return ((InboxTarget)target, index);
        }
        return null;
    }
}
