using Cordis.Core;
using Dsh.Session;

namespace Dsh.Session.Titles;

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
