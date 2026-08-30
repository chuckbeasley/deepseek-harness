namespace Dsh.AgentLoop.Tests;

/// <summary>Resume rehydrates the persisted log and continues turn numbering; the invariant guards dispatch.</summary>
public static class ResumeAndInvariantTests
{
    public static async Task RunAsync(Harness h)
    {
        // 1. Run a full turn, release the identity, and resume from the persisted log.
        var first = h.Loop.Create(new SessionId("session-resume"), new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
        var firstLoop = h.Loop.GetLoop(new SessionId("session-resume")) ?? throw new AssertionException("no loop published");
        firstLoop.Send(Harness.Prompt("first turn"), InboxTarget.NextTurn, wakeup: true);
        await firstLoop.WhenIdleAsync();
        var firstCount = first.Agent.Session.Events.Count;
        Assert.True(firstCount > 0, "the first turn must persist events");
        Assert.True(h.Persistence.Exists(new SessionId("session-resume")), "the session log must exist on disk");
        first.Dispose();
        h.Sessions.Remove(new SessionId("session-resume"));

        var resumed = h.Loop.Resume(new SessionId("session-resume"), new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
        var resumedLoop = h.Loop.GetLoop(new SessionId("session-resume")) ?? throw new AssertionException("no loop published on resume");
        Assert.Equal(firstCount, resumed.Agent.Session.Events.Count, "the stored log must rehydrate into the live session");
        resumedLoop.Send(Harness.Prompt("second turn"), InboxTarget.NextTurn, wakeup: true);
        await resumedLoop.WhenIdleAsync();

        Assert.Sequence(new long[] { 1, 2 }, resumed.Agent.Session.Events.OfType<TurnStartEvent>().Select(evt => evt.Turn).ToArray(), "resume must continue turn numbering from the persisted log");
        Assert.True(resumed.Agent.Session.Events.OfType<RequestHeaderEvent>().Any(evt => evt.Reason == RequestHeaderReason.Resume), "the resumed loop's first request must log a resume header");
        var stored = h.Persistence.Load(new SessionId("session-resume")) ?? throw new AssertionException("the persisted log must exist after resume");
        Assert.Equal(resumed.Agent.Session.Events.Count, stored.Events.Count, "the persisted log must cover both turns");

        // 2. The invariant must reject a loop-built request that diverges from the durable derivation.
        var session = resumed.Agent.Session;
        var header = session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header).Last();
        var bogus = new GenerateOptions(header.Config.Provider, header.Config.Model,
            new Message[] { Harness.Prompt("not in the log") }, System: header.System, Tools: header.Tools)
        {
            SessionId = session.Id.Value,
        };
        Assert.Throws<InvalidOperationException>(
            () => h.Llm.Stream(bogus, CancellationToken.None),
            "the invariant must reject a request that diverges from the durable derivation");

        // Positive control: an exact reconstruction passes the invariant and reaches the adapter.
        var callsBefore = h.Mock.CallCount;
        var exact = new GenerateOptions(header.Config.Provider, header.Config.Model, session.DeriveMessages(),
            System: header.System, Tools: header.Tools,
            Temperature: header.Config.Temperature, MaxTokens: header.Config.MaxTokens)
        {
            SessionId = session.Id.Value,
        };
        var chunkCount = 0;
        await foreach (var _ in h.Llm.Stream(exact, CancellationToken.None)) chunkCount++;
        Assert.True(h.Mock.CallCount > callsBefore, "an exact reconstruction must reach the adapter");
        Assert.True(chunkCount > 0, "the exact reconstruction must stream chunks");
    }
}
