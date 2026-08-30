using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Compaction;

/// <summary>
/// Tool-pairing balance over a session surface (port of tool-pairing.ts). Compaction changes
/// surface positions, so safe cuts are derived from tool-call/result content in current surface
/// order rather than step markers. The port folds the surface per call — the TS incremental
/// per-generation cache is an optimization, not a semantic difference.
/// </summary>
public static class ToolPairing
{
    /// <summary>Whether the cut immediately before a current surface sequence is tool-pairing balanced.</summary>
    /// <param name="session">the session whose surface is checked.</param>
    /// <param name="seq">the event sequence whose leading cut is checked.</param>
    /// <returns>true when no unanswered tool call crosses the cut.</returns>
    /// <exception cref="InvalidOperationException">when the seq is absent from the current surface or a tool result has no preceding open call (corrupt surface).</exception>
    public static bool BalancedBefore(Dsh.Session.Session session, long seq)
    {
        ArgumentNullException.ThrowIfNull(session);
        var nodes = SessionSurface.Nodes(session);
        var index = SessionSurface.IndexOfSeq(nodes, seq);
        if (index < 0)
        {
            throw new InvalidOperationException($"tool-pairing balance: surface seq {seq} not found");
        }
        long inProgress = 0;
        for (var i = 0; i < index; i++)
        {
            inProgress += EventDelta(EventForSeq(session, nodes[i]));
            if (inProgress < 0)
            {
                throw new InvalidOperationException(
                    $"tool-pairing balance: tool/result at surface seq {nodes[i]} has no matching tool-call (corrupt surface)");
            }
        }
        return inProgress == 0;
    }

    /// <summary>How one surface event changes the in-progress tool-call count.</summary>
    private static long EventDelta(SessionEvent evt) => evt switch
    {
        AssistantMessageEvent assistant => assistant.Message.Content.Count(block => block is ToolCallBlock),
        ToolResultEvent => -1,
        _ => 0,
    };

    /// <summary>Read and validate the event named by a surface sequence.</summary>
    private static SessionEvent EventForSeq(Dsh.Session.Session session, long seq)
    {
        var events = session.Events;
        if (seq < 0 || seq >= events.Count || events[(int)seq].Seq != seq)
        {
            throw new InvalidOperationException(
                $"tool-pairing balance: surface seq {seq} has no matching session event (corrupt surface)");
        }
        return events[(int)seq];
    }
}

/// <summary>
/// Turn alignment over the surface: a cut is turn-aligned when the node after it starts a new
/// turn. Port decision: the TS cuts between steps at tool-pairing boundaries, which its
/// between-step listener guarantees never split a turn; this port has no step-boundary hook, so
/// the region selection additionally requires whole-turn alignment — a compaction never splits a
/// turn, and a log with a single turn (or a single open turn) is never compactable.
/// </summary>
public static class TurnAlignment
{
    /// <summary>Whether the cut immediately before <paramref name="seq"/> is a turn boundary.</summary>
    /// <exception cref="InvalidOperationException">when the seq is absent from the current surface.</exception>
    public static bool IsTurnBoundaryCut(Dsh.Session.Session session, long seq)
    {
        ArgumentNullException.ThrowIfNull(session);
        var nodes = SessionSurface.Nodes(session);
        var index = SessionSurface.IndexOfSeq(nodes, seq);
        if (index < 0)
        {
            throw new InvalidOperationException($"turn alignment: surface seq {seq} not found");
        }
        if (index == 0) return true; // the cut before the first surface node is trivially a boundary
        return TurnOf(session, seq) != TurnOf(session, nodes[index - 1]);
    }

    /// <summary>The turn number owning a log position, or null before the first turn/start.</summary>
    private static long? TurnOf(Dsh.Session.Session session, long seq)
    {
        var events = session.Events;
        for (var i = (int)Math.Min(seq, events.Count - 1); i >= 0; i--)
        {
            if (events[i] is TurnStartEvent start) return start.Turn;
        }
        return null;
    }
}
