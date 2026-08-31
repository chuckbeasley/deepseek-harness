using Cordis.Core;
using Dsh.Llm;

namespace Dsh.Session;

/// <summary>
/// In-memory session store (ctx.sessions). Persistence is intentionally not implemented here —
/// persistence plugins subscribe to <c>session/event</c> and flush on dispose (Phase 2+ scope).
/// Creation registers one effect whose disposer detaches the session and emits
/// <c>session/disposed</c>, so context disposal unwinds live sessions.
/// </summary>
public sealed class SessionStore : Service
{
    private readonly Dictionary<SessionId, Session> _store = new();
    private int _counter;

    public SessionStore(Context ctx)
        : base(ctx, "sessions")
    {
    }

    /// <summary>
    /// Create a session owned by the calling fiber: disposing the context (or the returned
    /// registrations) detaches it and emits <c>session/disposed</c>.
    /// </summary>
    /// <param name="id">the session id; omitted, the store mints "session-&lt;n&gt;".</param>
    /// <param name="cwd">the session's workspace directory (defaults to the process cwd).</param>
    /// <param name="delegationDepth">the subagent delegation depth (0 for a top-level session).</param>
    /// <param name="parentSessionId">the parent session id for a subagent child.</param>
    /// <returns>the live session, already entered and announced.</returns>
    /// <exception cref="InvalidOperationException">when a session with <paramref name="id"/> already exists.</exception>
    public Session Create(SessionId? id = null, string? cwd = null, int delegationDepth = 0, string? parentSessionId = null)
    {
        var sessionId = id ?? new SessionId($"session-{++_counter}");
        if (_store.ContainsKey(sessionId))
        {
            throw new InvalidOperationException($"session \"{sessionId}\" already exists");
        }
        var session = new Session(sessionId, this, cwd ?? Environment.CurrentDirectory, delegationDepth, parentSessionId);
        Ctx.Effect(() =>
        {
            _store[sessionId] = session;
            EmitCreated(session);
            return new ActionDisposer(() =>
            {
                if (_store.TryGetValue(sessionId, out var current) && ReferenceEquals(current, session))
                {
                    _store.Remove(sessionId);
                    EmitDisposed(session);
                }
            });
        }, "sessions.create()");
        return session;
    }

    /// <summary>Look up a live session.</summary>
    public Session? Get(SessionId id) => _store.TryGetValue(id, out var session) ? session : null;

    /// <summary>
    /// Detach a live session by id, emitting <c>session/disposed</c> (idempotent). Used by the
    /// resume flow to release an identity before its stored log rehydrates a fresh session.
    /// </summary>
    /// <returns>whether the session was still attached.</returns>
    public bool Remove(SessionId id)
    {
        if (!_store.TryGetValue(id, out var session)) return false;
        _store.Remove(id);
        EmitDisposed(session);
        return true;
    }

    /// <summary>All live sessions, in creation order.</summary>
    public IReadOnlyList<Session> List() => _store.Values.ToArray();

    /// <summary>
    /// Post-commit append feed. Contained per dispatch: a throwing observer is logged and cannot
    /// fail the committed append (the port's Emit aborts remaining listeners on a throw, so the
    /// store isolates each publication).
    /// </summary>
    internal void Publish(Session session, SessionEvent evt)
    {
        try
        {
            Ctx.Emit("session/event", session, evt);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"session \"{session.Id}\": session/event listener threw: {error.Message}");
        }
    }

    private void EmitCreated(Session session)
    {
        try
        {
            Ctx.Emit("session/created", session);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"session \"{session.Id}\": session/created listener threw: {error.Message}");
        }
    }

    private void EmitDisposed(Session session)
    {
        try
        {
            Ctx.Emit("session/disposed", session);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"session \"{session.Id}\": session/disposed listener threw: {error.Message}");
        }
    }
}
