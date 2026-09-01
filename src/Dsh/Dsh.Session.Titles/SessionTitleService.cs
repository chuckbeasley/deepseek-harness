using Harness.Cordis.Core;
using Harness.AgentLoop;
using Harness.Llm;
using Harness.Session;

namespace Harness.Session.Titles;

/// <summary>
/// ctx.sessionTitle: the session-title capability. Exactly one typed
/// <see cref="ISessionTitleProvider"/> may be registered — a second registration fails loud.
/// <see cref="TitleFor"/> fails explicitly when no provider is registered.
/// </summary>
public sealed class SessionTitleService : Service
{
    private ISessionTitleProvider? _provider;

    /// <summary>Create and install the service as <c>sessionTitle</c>.</summary>
    /// <param name="ctx">the context that owns the service.</param>
    public SessionTitleService(Context ctx)
        : base(ctx, "sessionTitle")
    {
    }

    /// <summary>Whether a provider is registered.</summary>
    public bool HasProvider => _provider is not null;

    /// <summary>
    /// Register the sole session-title provider. Typed service registration: only one provider is
    /// allowed, and a second registration fails loud instead of silently replacing the first.
    /// </summary>
    /// <param name="provider">the provider to install.</param>
    /// <exception cref="InvalidOperationException">when a provider is already registered.</exception>
    public void RegisterProvider(ISessionTitleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_provider is not null)
        {
            throw new InvalidOperationException(
                "a session-title provider is already registered; only one provider is allowed");
        }
        _provider = provider;
    }

    /// <summary>Derive the session's title through the registered provider.</summary>
    /// <param name="session">the session whose title is derived.</param>
    /// <returns>the title, or <c>null</c> when the provider derives none.</returns>
    /// <exception cref="InvalidOperationException">when no provider is registered.</exception>
    public string? TitleFor(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_provider is null)
        {
            throw new InvalidOperationException("no session-title provider is registered");
        }
        return _provider.Generate(session);
    }
}

/// <summary>Resolved fallback-title limits (the TS base-bundle spellings).</summary>
public sealed record SessionTitleConfig
{
    /// <summary>Whitespace-delimited word cap for the fallback title.</summary>
    public int FallbackMaxWords { get; init; } = 5;

    /// <summary>UTF-8 byte cap for the fallback title.</summary>
    public int FallbackMaxBytes { get; init; } = 40;

    /// <summary>UTF-8 byte cap for any accepted title.</summary>
    public int MaxTitleBytes { get; init; } = 80;
}

/// <summary>
/// The fallback-capable session-title service (port of the TS session-title service): the first
/// eligible direct-human <c>user/message</c> queues the deterministic fallback, and the
/// <c>agent/request</c> waterfall flushes it as a durable <c>session/title</c> event — that point
/// fires after the whole pre-step message batch commits (including hook-injected contexts) and
/// before the request header, reproducing the recorded fixture position without a microtask
/// deferral. The asynchronous provider surface stays deferred.
/// </summary>
public sealed class FallbackSessionTitleService : Service
{
    private readonly SessionTitleConfig _config;
    private readonly Dictionary<SessionId, PendingTitle> _pending = new();

    /// <summary>Create the service, register the title event type, and follow live user/message appends.</summary>
    public FallbackSessionTitleService(Context ctx, SessionTitleConfig? config = null)
        : base(ctx, "sessionTitle")
    {
        TitleEventTypes.Register();
        _config = config ?? new SessionTitleConfig();
        Ctx.On("session/event", (Delegate)(Action<Session, SessionEvent>)((session, evt) =>
        {
            if (evt is UserMessageEvent { Message.Source: UserSource } user
                && !session.Events.OfType<SessionTitleEvent>().Any())
            {
                var text = string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text));
                var title = TitleText.FallbackTitle(text, _config.FallbackMaxWords, _config.FallbackMaxBytes);
                if (title.Length > 0)
                {
                    _pending[session.Id] = new PendingTitle(evt.Seq, title);
                }
            }
        }));
        Ctx.On("agent/request", new Func<RequestProposal, Func<Task<LlmCallConfig>>, Task<LlmCallConfig>>((proposal, next) =>
        {
            if (_pending.Remove(proposal.Agent.Session.Id, out var pending)
                && !proposal.Agent.Session.Events.OfType<SessionTitleEvent>().Any())
            {
                proposal.Agent.Session.Append(new SessionTitleEvent
                {
                    Title = pending.Title,
                    MessageSeqs = new long[] { pending.MessageSeq },
                    Source = new FallbackTitleSource(),
                });
            }
            return next();
        }));
    }

    private sealed record PendingTitle(long MessageSeq, string Title);
}
