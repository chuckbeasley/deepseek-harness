namespace Harness.Cordis.Core;

/// <summary>
/// Inspection surface over the services, listeners, and effects registered on a context (Phase 0
/// port of the vendored Cordis RegistryService surface relevant to the spike; the plugin-runtime
/// registry behind <c>ctx.plugin</c> lands with the loader in Phase 1).
/// </summary>
public sealed class RegistryService
{
    private readonly Context _ctx;

    internal RegistryService(Context ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Keys currently registered in the service repository, in registration order.</summary>
    public IReadOnlyList<string> ServiceKeys => _ctx.ServiceKeys;

    /// <summary>Number of registered listeners for one event.</summary>
    public int ListenerCount(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _ctx.Events.ListenerCount(name);
    }

    /// <summary>Metadata for the effects currently owned by the root fiber.</summary>
    public IReadOnlyList<EffectMeta> Effects => _ctx.Fiber.GetEffects();
}
