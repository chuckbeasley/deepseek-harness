namespace Dsh.AgentLoop;

/// <summary>
/// Tracks the last retained runtime-context snapshot without owning its commit (port of the TS
/// RuntimeContextProjection). The port is live-only: the retained snapshot follows authoritative
/// <c>session/event</c> appends, and replacement-surface tracking (the TS surface-node check)
/// arrives with the surface projection port.
/// </summary>
public sealed class RuntimeContextProjection
{
    private (long Seq, string? Text)? _retained;
    private bool _initialized;

    /// <summary>
    /// Restore projection state once from the session's existing log, then follow live appends.
    /// </summary>
    /// <param name="ownerCtx">the context whose <c>session/event</c> stream is observed.</param>
    /// <param name="session">the session receiving projected messages.</param>
    public RuntimeContextProjection(Context ownerCtx, Dsh.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(ownerCtx);
        ArgumentNullException.ThrowIfNull(session);
        for (var index = session.Events.Count - 1; index >= 0; index--)
        {
            if (session.Events[index] is not UserMessageEvent { Message: { Source: PluginSource { Plugin: AgentLoopConstants.RuntimeContextSource } } } owned)
            {
                continue;
            }
            _initialized = true;
            _retained = (owned.Seq, TextOf(owned.Message));
            break;
        }
        ownerCtx.On("session/event", (Delegate)(Action<Dsh.Session.Session, SessionEvent>)((subject, evt) =>
        {
            if (!ReferenceEquals(subject, session)) return;
            if (evt is UserMessageEvent { Message: { Source: PluginSource { Plugin: AgentLoopConstants.RuntimeContextSource } } } committed)
            {
                _initialized = true;
                _retained = (committed.Seq, TextOf(committed.Message));
            }
        }));
    }

    /// <summary>
    /// Create an uncommitted snapshot message only when the retained value differs from
    /// <paramref name="current"/>: the first non-empty context projects once, a change projects
    /// again, and emptying projects the cleared marker once.
    /// </summary>
    /// <param name="current">fully rendered dynamic context.</param>
    /// <param name="sections">named contributions that formed the current snapshot.</param>
    /// <returns>a candidate user message, or <c>null</c> when no update is needed.</returns>
    public UserMessage? Project(string current, IReadOnlyList<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (!_initialized && current.Length == 0) return null;
        var snapshot = current.Length == 0 ? AgentLoopConstants.ClearedRuntimeContext : current;
        if (_retained?.Text == snapshot) return null;
        var source = sections.Count == 0
            ? new PluginSource { Plugin = AgentLoopConstants.RuntimeContextSource }
            : new PluginSource { Plugin = AgentLoopConstants.RuntimeContextSource, Form = "snapshot", Sections = sections.ToArray() };
        return Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(snapshot) }, source);
    }

    private static string? TextOf(UserMessage message)
        => message.Content.Count == 1 && message.Content[0] is TextBlock text ? text.Text : null;
}
