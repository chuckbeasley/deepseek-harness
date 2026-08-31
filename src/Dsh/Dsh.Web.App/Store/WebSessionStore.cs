using Cordis.Core;
using Dsh.Session;

namespace Dsh.Web.App.Store;

/// <summary>One session entry in the store: identity and the live transcript events.</summary>
public sealed class WebSessionEntry
{
    internal WebSessionEntry(Dsh.Session.Session session)
    {
        Session = session;
    }

    /// <summary>The live session this entry projects.</summary>
    public Dsh.Session.Session Session { get; }

    /// <summary>Every committed event, in log order.</summary>
    public IReadOnlyList<SessionEvent> Events => Session.Events;

    /// <summary>The last assistant text, for the session list summary.</summary>
    public string? Summary => Session.Events
        .OfType<AssistantMessageEvent>()
        .LastOrDefault()
        ?.Message
        .Content
        .OfType<Dsh.Llm.TextBlock>()
        .Select(block => block.Text)
        .FirstOrDefault();
}

/// <summary>
/// The web session store (the store layer of the Phase-5 shell): an observable projection of the
/// ported session store over the Cordis event stream. Components subscribe through
/// <see cref="Changed"/>, and every change notification happens after the committed append, so
/// renders always observe committed state.
/// </summary>
public sealed class WebSessionStore : IDisposable
{
    private readonly Context _ctx;
    private readonly Dictionary<SessionId, WebSessionEntry> _entries = new();
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = new();
    private event Action? _changed;

    /// <summary>Create the store and subscribe to the session event stream.</summary>
    public WebSessionStore(Context ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        var sessions = ctx.Get<SessionStore>("sessions");
        if (sessions is not null)
        {
            foreach (var session in sessions.List()) _entries[session.Id] = new WebSessionEntry(session);
        }
        _subscriptions.Add(ctx.On<Dsh.Session.Session>("session/created", OnCreated));
        _subscriptions.Add(ctx.On<Dsh.Session.Session>("session/disposed", OnDisposed));
        _subscriptions.Add(ctx.On("session/event", new Action<Dsh.Session.Session, SessionEvent>((_, _) => NotifyChanged())));
    }

    /// <summary>Every live session entry, in creation order.</summary>
    public IReadOnlyList<WebSessionEntry> List()
    {
        lock (_gate) return _entries.Values.ToArray();
    }

    /// <summary>One session entry, or <c>null</c> when the session is not live.</summary>
    public WebSessionEntry? Get(SessionId id)
    {
        lock (_gate) return _entries.GetValueOrDefault(id);
    }

    /// <summary>Subscribe to store changes; the handler runs after every committed append or lifecycle change.</summary>
    public IDisposable Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _changed += handler;
        return new ActionDisposer(() => _changed -= handler);
    }

    /// <summary>Unsubscribe every Cordis listener.</summary>
    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private void OnCreated(Dsh.Session.Session session)
    {
        lock (_gate) _entries[session.Id] = new WebSessionEntry(session);
        NotifyChanged();
    }

    private void OnDisposed(Dsh.Session.Session session)
    {
        lock (_gate) _entries.Remove(session.Id);
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var handler = _changed;
        if (handler is null) return;
        foreach (var invocation in handler.GetInvocationList())
        {
            try
            {
                ((Action)invocation)();
            }
            catch (Exception error)
            {
                _ctx.Logger.Warn($"web: a session store subscriber threw: {error.Message}");
            }
        }
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync cleanups.</summary>
internal sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}



