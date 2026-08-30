using Cordis.Core;
using Dsh.Agent;

namespace Dsh.Scope;

/// <summary>
/// Opaque, identity-compared scope key (port of the TS <c>ScopeKey = object</c>). The port's scope
/// key is the subject <see cref="Agent"/> itself; this type is retained for future non-agent scopes.
/// </summary>
public sealed class ScopeKey
{
    private ScopeKey()
    {
    }

    /// <summary>Mint one distinct key.</summary>
    public static ScopeKey New() => new();
}

/// <summary>
/// Scoped-registration primitive (port of the TS dsh-scope semantics). Registering through an
/// agent's scoped context owns the contribution with that agent: it unwinds when the agent's scope
/// disposes, and scoped event listeners receive only that agent's <c>agent/*</c> dispatches.
/// </summary>
public static class AgentScoped
{
    /// <summary>
    /// Register an effect on <paramref name="agent"/>'s scoped context. The effect's setup runs
    /// immediately; its cleanup runs when the agent's scope unwinds (handle disposal or registry
    /// context disposal).
    /// </summary>
    /// <param name="agent">the agent whose lifecycle owns the effect.</param>
    /// <param name="effect">the effect body; returns the cleanup disposer, or <c>null</c>.</param>
    /// <param name="label">effect label shown in fiber diagnostics.</param>
    /// <returns>a single-shot disposer running the cleanup.</returns>
    public static IDisposable RegisterEffect(Dsh.Agent.Agent agent, Func<IDisposable?> effect, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(effect);
        return agent.Ctx.Effect(effect, label);
    }

    /// <summary>
    /// Async-teardown variant of <see cref="RegisterEffect"/>.
    /// </summary>
    public static IAsyncDisposable RegisterEffectAsync(Dsh.Agent.Agent agent, Func<IAsyncDisposable?> effect, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(effect);
        return agent.Ctx.EffectAsync(effect, label);
    }

    /// <summary>
    /// Register a service in the agent's scoped context; the entry is removed when the agent's
    /// scope unwinds.
    /// </summary>
    public static void SetService<T>(Dsh.Agent.Agent agent, string key, T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(agent);
        agent.Ctx.Set(key, service);
    }

    /// <summary>
    /// Listen to one <c>agent/*</c> event, receiving only this agent's dispatches (the port of the
    /// TS <c>scopeTarget</c> filter). The listener is registered on the agent's owner context where
    /// its events dispatch; dispatches whose payload subject is a different agent are dropped.
    /// </summary>
    /// <typeparam name="T">the event's payload record type.</typeparam>
    /// <param name="agent">the agent whose dispatches are admitted.</param>
    /// <param name="name">the <c>agent/*</c> event name.</param>
    /// <param name="listener">the typed payload listener.</param>
    /// <param name="options">placement and filtering options.</param>
    /// <returns>a disposer removing the listener.</returns>
    public static IDisposable OnAgentEvent<T>(Dsh.Agent.Agent agent, string name, Action<T> listener, EventOptions? options = null)
        where T : IAgentEventPayload
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(listener);
        return agent.Owner.On(name, new Action<object?>(payload =>
        {
            if (payload is not T typed || !ReferenceEquals(typed.Agent, agent)) return;
            listener(typed);
        }), options);
    }
}
