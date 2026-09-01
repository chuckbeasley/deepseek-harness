using Harness.Cordis.Core;
using Harness.Session;

namespace Harness.Agent.Tests;

/// <summary>Registry registration, disposal, and lifecycle events.</summary>
internal static class RegistryTests
{
    public static void RegistryIsService()
    {
        var (ctx, registry, _) = Harness.Boot();
        Assert.True(ReferenceEquals(registry, ctx.Get<AgentRegistry>("agents")), "ctx service 'agents' must hold the registry");
    }

    public static void RegisterThenDisposeRemoves()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var handle = registry.Register(agent);
        Assert.True(ReferenceEquals(registry.Get(agent.Id), agent), "the agent must be live after register");
        handle.Dispose();
        Assert.Equal(null, registry.Get(agent.Id));
        Assert.True(!registry.Contains(agent.Id), "the registry must not contain the disposed agent");
    }

    public static void ContextDisposalRemoves()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var handle = registry.Register(agent);
        ctx.Dispose();
        Assert.Equal(null, registry.Get(agent.Id));
        // The handle stays safe to dispose after the context already detached the agent.
        handle.Dispose();
    }

    public static void RegisterDuplicateThrows()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        registry.Register(agent);
        Assert.Throws<InvalidOperationException>(() => registry.Register(agent), "a second register of the same agent must fail loud");
    }

    public static void CreatedAndDisposedEvents()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        var agent = Harness.NewAgent(ctx, sessions, "agent-1");
        var created = new List<AgentCreatedPayload>();
        var disposed = new List<AgentDisposedPayload>();
        ctx.On<AgentCreatedPayload>(AgentEvents.Created, created.Add);
        ctx.On<AgentDisposedPayload>(AgentEvents.Disposed, disposed.Add);
        var handle = registry.Register(agent);
        Assert.Equal(1, created.Count);
        Assert.True(ReferenceEquals(created[0].Agent, agent), "created must carry the exact agent");
        handle.Dispose();
        Assert.Equal(1, disposed.Count);
        Assert.True(ReferenceEquals(disposed[0].Agent, agent), "disposed must carry the exact agent");
    }

    public static void RegisterOnForeignContextThrows()
    {
        var (ctx, registry, sessions) = Harness.Boot();
        using var foreign = new Context();
        var agent = Harness.NewAgent(foreign, sessions, "agent-1");
        Assert.Throws<InvalidOperationException>(() => registry.Register(agent), "register must reject an agent owned by another context");
    }
}
