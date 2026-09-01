using Harness.Llm;
using Harness.Scope;

namespace Harness.Agent.Tests;

/// <summary>Scoped registration unwinds with the agent's lifecycle.</summary>
internal static class ScopeTests
{
    public static void EffectDisposesWithAgent()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var handle = registry.Register(agent);
        var cleaned = false;
        AgentScoped.RegisterEffect(agent, () =>
        {
            cleaned = false;
            return new ActionDisposer(() => cleaned = true);
        }, "scope-test");
        Assert.True(!cleaned, "the scoped cleanup must not run before agent disposal");
        handle.Dispose();
        Assert.True(cleaned, "the scoped cleanup must run when the agent's scope unwinds");
    }

    public static void EffectDisposesOnRegistryContextDisposal()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        registry.Register(agent);
        var cleaned = false;
        AgentScoped.RegisterEffect(agent, () => new ActionDisposer(() => cleaned = true), "scope-test");
        ctx.Dispose();
        Assert.True(cleaned, "the scoped cleanup must run when the registry's context disposes");
    }

    public static void ScopedServiceRemovedWithAgent()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var handle = registry.Register(agent);
        AgentScoped.SetService(agent, "scoped-value", "payload");
        Assert.Equal("payload", agent.Ctx.Get<string>("scoped-value"));
        handle.Dispose();
        Assert.Equal(null, agent.Ctx.Get<string>("scoped-value"));
    }

    public static void ScopedListenerReceivesOwnAgentOnly()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var first = Harness.NewAgent(ctx, sessions, "agent-1");
        var second = Harness.NewAgent(ctx, sessions, "agent-2");
        registry.Register(first);
        registry.Register(second);
        var seen = new List<AgentStatusPayload>();
        AgentScoped.OnAgentEvent<AgentStatusPayload>(first, AgentEvents.Status, seen.Add);
        second.SetStatus(AgentStatus.Running);
        first.SetStatus(AgentStatus.Running);
        Assert.Equal(1, seen.Count);
        Assert.True(ReferenceEquals(seen[0].Agent, first), "the scoped listener must only receive its own agent's dispatches");
    }
}
