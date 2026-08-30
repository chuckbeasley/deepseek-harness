using Dsh.Llm;

namespace Dsh.Session;

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
}
