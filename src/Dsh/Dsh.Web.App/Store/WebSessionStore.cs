using Harness.Cordis.Core;
using Harness.Agent;
using Harness.AgentLoop;
using Harness.Session;

namespace Harness.Web.App.Store;

/// <summary>One session entry in the store: identity, the live transcript events, and the live agent state.</summary>
public sealed class WebSessionEntry
{
    internal WebSessionEntry(Harness.Session.Session session)
    {
        Session = session;
    }

    /// <summary>The live session this entry projects.</summary>
    public Harness.Session.Session Session { get; }

    /// <summary>Every committed event, in log order.</summary>
    public IReadOnlyList<SessionEvent> Events => Session.Events;

    /// <summary>The last assistant text, for the session list summary.</summary>
    public string? Summary => Session.Events
        .OfType<AssistantMessageEvent>()
        .LastOrDefault()
        ?.Message
        .Content
        .OfType<Harness.Llm.TextBlock>()
        .Select(block => block.Text)
        .FirstOrDefault();

    /// <summary>Whether the session's agent driver is mid-activity (agent/status).</summary>
    public bool Running { get; internal set; }

    /// <summary>Messages awaiting a turn or step boundary in the live inbox.</summary>
    public int Queued { get; internal set; }

    /// <summary>The last agent failure message, cleared when a new activity starts.</summary>
    public string? Error { get; internal set; }
}

/// <summary>
/// The web session store (the store layer of the Phase-5 shell): an observable projection of the
/// ported session store and the live agent state over the Cordis event stream. Components
/// subscribe through <see cref="Changed"/>, and every change notification happens after the
/// committed append, so renders always observe committed state.
/// </summary>
public sealed class WebSessionStore : IDisposable
{
    private readonly Context _ctx;
    private readonly Dictionary<SessionId, WebSessionEntry> _entries = new();
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = new();
    private event Action? _changed;

    /// <summary>Create the store and subscribe to the session and agent event streams.</summary>
    public WebSessionStore(Context ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        var sessions = ctx.Get<SessionStore>("sessions");
        if (sessions is not null)
        {
            foreach (var session in sessions.List()) _entries[session.Id] = new WebSessionEntry(session);
        }
        _subscriptions.Add(ctx.On<Harness.Session.Session>("session/created", OnCreated));
        _subscriptions.Add(ctx.On<Harness.Session.Session>("session/disposed", OnDisposed));
        _subscriptions.Add(ctx.On("session/event", new Action<Harness.Session.Session, SessionEvent>((_, _) => NotifyChanged())));
        var agents = ctx.Get<AgentRegistry>("agents");
        if (agents is not null)
        {
            foreach (var agent in agents.List())
            {
                if (_entries.TryGetValue(agent.Session.Id, out var entry))
                {
                    entry.Running = agent.Status == AgentStatus.Running;
                    entry.Queued = agent.Inbox.NextTurn.Count + agent.Inbox.NextStep.Count;
                }
            }
            _subscriptions.Add(ctx.On("agent/status", new Action<AgentStatusPayload>(OnStatus)));
            _subscriptions.Add(ctx.On("agent/inbox/inserted", new Action<AgentInboxInsertedPayload>(payload => OnInboxChanged(payload.Agent))));
            _subscriptions.Add(ctx.On("agent/inbox/claimed", new Action<AgentInboxClaimedPayload>(payload => OnInboxChanged(payload.Agent))));
            _subscriptions.Add(ctx.On("agent/inbox/discarded", new Action<AgentInboxDiscardedPayload>(payload => OnInboxChanged(payload.Agent))));
            _subscriptions.Add(ctx.On("agent/error", new Action<AgentErrorPayload>(OnError)));
        }
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

    private void OnCreated(Harness.Session.Session session)
    {
        lock (_gate) _entries[session.Id] = new WebSessionEntry(session);
        NotifyChanged();
    }

    private void OnDisposed(Harness.Session.Session session)
    {
        lock (_gate) _entries.Remove(session.Id);
        NotifyChanged();
    }

    private void OnStatus(AgentStatusPayload payload)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(payload.Agent.Session.Id, out var entry)) return;
            entry.Running = payload.Status == AgentStatus.Running;
            if (entry.Running) entry.Error = null;
        }
        NotifyChanged();
    }

    private void OnInboxChanged(Harness.Agent.Agent agent)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(agent.Session.Id, out var entry)) return;
            entry.Queued = agent.Inbox.NextTurn.Count + agent.Inbox.NextStep.Count;
        }
        NotifyChanged();
    }

    private void OnError(AgentErrorPayload payload)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(payload.Agent.Session.Id, out var entry)) return;
            entry.Error = payload.Error.Message;
        }
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



