namespace Dsh.AgentLoop.Tests;

/// <summary>Dynamic runtime context projects one snapshot per change into the claimed step input.</summary>
public static class RuntimeContextTests
{
    public static async Task RunAsync(Harness h)
    {
        var provider = new Func<Task<RuntimeContextPart>>(
            () => Task.FromResult(new RuntimeContextPart("Current working directory: C:\\work", new[] { new NamedSection("cwd", "Current working directory: C:\\work") })));
        using var registration = h.Loop.RegisterContextProvider(provider);
        var (_, agent, loop) = h.CreateAgent("session-context");

        loop.Send(Harness.Prompt("first"), InboxTarget.NextTurn, wakeup: true);
        await loop.WhenIdleAsync();

        var snapshots = OwnedSnapshots(agent);
        Assert.Equal(1, snapshots.Length, "the first step must project one runtime-context snapshot");
        var source = (PluginSource)snapshots[0].Message.Source;
        Assert.Equal("snapshot", source.Form, "the snapshot must carry the snapshot form");
        Assert.Sequence(new[] { "cwd" }, (source.Sections ?? Array.Empty<NamedSection>()).Select(section => section.Name).ToArray(),
            "the snapshot must attribute its contributing sections");

        loop.Send(Harness.Prompt("second"), InboxTarget.NextTurn, wakeup: true);
        await loop.WhenIdleAsync();
        Assert.Equal(1, OwnedSnapshots(agent).Length, "unchanged context must not project a second snapshot");
    }

    private static UserMessageEvent[] OwnedSnapshots(Dsh.Agent.Agent agent)
        => agent.Session.Events.OfType<UserMessageEvent>()
            .Where(evt => evt.Message.Source is PluginSource { Plugin: AgentLoopConstants.RuntimeContextSource })
            .ToArray();
}
