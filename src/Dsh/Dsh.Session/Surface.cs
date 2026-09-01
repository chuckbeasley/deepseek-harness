using Harness.Llm;

namespace Harness.Session;

/// <summary>
/// Surface layer over the session event log: which events produce LLM messages and how each one
/// projects. The append-only log remains the source of truth; this fold derives model history.
/// </summary>
public static class Surface
{
    /// <summary>The three message-producing event types.</summary>
    private static readonly HashSet<string> SurfaceEventTypes = new(StringComparer.Ordinal)
    {
        "user/message",
        "assistant/message",
        "tool/result",
    };

    /// <summary>Whether an event type can join the model-visible surface.</summary>
    public static bool IsSurfaceEligibleType(string type) => SurfaceEventTypes.Contains(type);

    /// <summary>Whether an event can join the model-visible surface.</summary>
    public static bool IsSurfaceEligibleType(SessionEvent e)
        => e is UserMessageEvent or AssistantMessageEvent or ToolResultEvent;

    /// <summary>
    /// Project a single event into the LLM message it derives to, or null when it produces none — a
    /// non-surface event (boundary, chunk, log-only record) or an empty-content assistant/message.
    /// </summary>
    public static Message? DeriveEventMessage(SessionEvent e) => e switch
    {
        // Ordinary prompts and injected context project verbatim in user role.
        UserMessageEvent user => user.Message,
        // Skip an empty-content assistant/message: it exists only to host a max-tokens step's usage.
        AssistantMessageEvent assistant when assistant.Message.Content.Count > 0 => assistant.Message,
        ToolResultEvent tool => tool.Message,
        _ => null,
    };

    /// <summary>
    /// One live surface node: the event seq and its derived message. A replace-op checkpoint
    /// event's node carries the checkpoint event's own seq.
    /// </summary>
    public readonly record struct SurfaceNode(long Seq, Message Message);

    /// <summary>
    /// Fold the log into the current surface, applying replace ops in log order: a
    /// user/message with <see cref="SurfaceOpReplace"/> shadows the inclusive current-surface
    /// seq range [Start, End] and inserts its own message at the first shadowed position (the
    /// port of the TS surface fold). A replace naming a range absent from the current surface is
    /// log corruption and fails loud.
    /// </summary>
    public static List<SurfaceNode> Fold(IEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var nodes = new List<SurfaceNode>();
        foreach (var evt in events)
        {
            if (evt is UserMessageEvent user && user.SurfaceOp is SurfaceOpReplace replace)
            {
                var startIdx = nodes.FindIndex(node => node.Seq == replace.Start);
                var endIdx = nodes.FindIndex(node => node.Seq == replace.End);
                if (startIdx < 0 || endIdx < 0 || startIdx > endIdx)
                {
                    throw new InvalidOperationException(
                        $"surface fold: replace at seq {evt.Seq} has invalid current range {replace.Start}-{replace.End}");
                }
                nodes.RemoveRange(startIdx, endIdx - startIdx + 1);
                nodes.Insert(startIdx, new SurfaceNode(evt.Seq, user.Message));
                continue;
            }
            var message = DeriveEventMessage(evt);
            if (message is not null) nodes.Add(new SurfaceNode(evt.Seq, message));
        }
        return nodes;
    }
}
