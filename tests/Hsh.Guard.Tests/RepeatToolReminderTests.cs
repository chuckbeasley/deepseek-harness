namespace Harness.Guard.Tests;

/// <summary>
/// Behavior suite for the repeat-tool-call guard (ported from the TS spec): chain semantics
/// (identical / different-tracked / excluded-transparent / per-agent / user-message reset /
/// fresh-agent), threshold escalation incl. the thresholds[0] gentle-text rule, the arguments
/// preview cap, durability in the persisted log, and fail-loud config validation — driven through
/// a real agent loop against a scripted mock adapter (no network).
/// </summary>
public static class RepeatToolReminderTests
{
    public static async Task TripsExactlyAtTheThreshold()
    {
        await using var h = Harness.Create();
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.Tool("c2", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.Tool("c3", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.Tool("c4", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.Tool("c5", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        var found = Harness.Reminders(agent);
        Assert.Equal(2, found.Count, "reminders must arrive exactly at the default thresholds 3 and 5");
        var gentle = Harness.TextOf(found[0]);
        Assert.Contains("repeating the exact same tool call", gentle, "the first reminder must be the gentle text");
        Assert.Equal("repeat-tool-reminder", ((PluginSource)found[0].Message.Source).Plugin, "the reminder must carry the guard plugin source");
        Assert.Equal("notice", ((PluginSource)found[0].Message.Source).Form, "the reminder must carry the notice form");
        var detailed = Harness.TextOf(found[1]);
        Assert.Contains("- tool: probe", detailed, "the detailed reminder must name the repeated tool");
        Assert.Contains("consecutive_calls: 5", detailed, "the detailed reminder must carry the run length");
        Assert.Contains("{\"q\":\"same\"}", detailed, "the detailed reminder must quote the canonical arguments");
    }

    public static async Task NoReminderBeforeTheThreshold()
    {
        await using var h = Harness.Create();
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.Tool("c2", "probe", "{\"q\":\"same\"}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        Assert.Equal(0, Harness.Reminders(agent).Count, "two repeats must not trip the first threshold of 3");
    }

    public static async Task ReminderIsDurable()
    {
        await using var h = Harness.Create();
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{}"),
            ScriptedResponse.Tool("c2", "probe", "{}"),
            ScriptedResponse.Tool("c3", "probe", "{}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        var live = Harness.Reminders(agent);
        Assert.Equal(1, live.Count, "the third repeat must trip the gentle threshold");
        var stored = h.Persistence.Load(new SessionId("a1"))
            ?? throw new AssertionException("the persisted log must exist after the turn");
        Assert.Equal(agent.Session.Events.Count, stored.Events.Count, "the persisted log must hold every event");
        var persisted = stored.Events.OfType<UserMessageEvent>()
            .Where(evt => evt.Message.Source is PluginSource { Plugin: RepeatToolReminderGuard.GuardName })
            .ToArray();
        Assert.Equal(1, persisted.Length, "the reminder must be durable in the persisted log");
        Assert.Equal(Harness.TextOf(live[0]), Harness.TextOf(persisted[0]), "the persisted reminder must replay the identical text");
    }

    public static async Task DistinctToolsDoNotTrip()
    {
        await using var h = Harness.Create();
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c2", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c3", "other", "{}"),
            ScriptedResponse.Tool("c4", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c5", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        // probe runs 1,2 then 1,2 across the different-call reset — never 3 consecutive.
        Assert.Equal(0, Harness.Reminders(agent).Count, "a different tracked call must reset the chain");
    }

    public static async Task CustomThresholdsAreNormalizedAscending()
    {
        await using var h = Harness.Create(new RepeatToolReminderConfig { Thresholds = new[] { 4, 2 } });
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{}"),
            ScriptedResponse.Tool("c2", "probe", "{}"),
            ScriptedResponse.Tool("c3", "probe", "{}"),
            ScriptedResponse.Tool("c4", "probe", "{}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        var found = Harness.Reminders(agent);
        Assert.Equal(2, found.Count, "the normalized thresholds [2, 4] must trip twice");
        Assert.Contains("repeating the exact same tool call", Harness.TextOf(found[0]),
            "the gentle text is keyed to thresholds[0] (2), not the literal 3");
        Assert.Contains("consecutive_calls: 4", Harness.TextOf(found[1]), "the second reminder must be detailed at 4");
    }

    public static async Task ExcludedToolsAreTransparent()
    {
        await using var h = Harness.Create(new RepeatToolReminderConfig { Exclude = new[] { "other" } });
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c2", "other", "{}"),
            ScriptedResponse.Tool("c3", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c4", "other", "{}"),
            ScriptedResponse.Tool("c5", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        var found = Harness.Reminders(agent);
        Assert.Equal(1, found.Count, "excluded calls must neither count nor reset, so the third probe trips");
        Assert.Contains("repeating the exact same tool call", Harness.TextOf(found[0]), "the tripped reminder must be gentle");
    }

    public static async Task IncludePatternsTrackOnlyMatchingTools()
    {
        await using var h = Harness.Create(new RepeatToolReminderConfig { Include = new[] { "pro*" } });
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "other", "{}"),
            ScriptedResponse.Tool("c2", "other", "{}"),
            ScriptedResponse.Tool("c3", "other", "{}"),
            ScriptedResponse.Tool("c4", "probe", "{}"),
            ScriptedResponse.Tool("c5", "probe", "{}"),
            ScriptedResponse.Tool("c6", "probe", "{}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        var found = Harness.Reminders(agent);
        Assert.Equal(1, found.Count, "only tracked tools may trip the chain; three identical other calls must not");
        Assert.Contains("repeating the exact same tool call", Harness.TextOf(found[0]), "the tripped reminder must be gentle");
    }

    public static async Task DetailedReminderCapsTheArgumentsPreview()
    {
        var bigPayload = new string('x', 400);
        await using var h = Harness.Create(new RepeatToolReminderConfig { Thresholds = new[] { 2, 3 }, ArgumentsPreviewChars = 24 });
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"body\":\"" + bigPayload + "\"}"),
            ScriptedResponse.Tool("c2", "probe", "{\"body\":\"" + bigPayload + "\"}"),
            ScriptedResponse.Tool("c3", "probe", "{\"body\":\"" + bigPayload + "\"}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();

        var found = Harness.Reminders(agent);
        Assert.Equal(2, found.Count, "gentle at 2, detailed at 3 — full-key matching must survive the cap");
        var detailed = Harness.TextOf(found[1]);
        // Canonical of {"body":"<400 x>"} is 411 chars; a 24-char head keeps 15 payload chars.
        Assert.Contains("- arguments: {\"body\":\"" + new string('x', 15), detailed, "the detailed reminder must quote the capped head");
        Assert.Contains("… (+387 more chars)", detailed, "the detailed reminder must mark how much was omitted");
        Assert.NotContains(bigPayload, detailed, "the full payload must never ride into the reminder");
    }

    public static async Task UserMessageResetsTheChain()
    {
        await using var h = Harness.Create();
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c2", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("turn one done"),
            ScriptedResponse.Tool("c3", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("c4", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done"));
        var (_, agent, loop) = h.CreateAgent("a1");
        loop.Followup(Harness.Prompt("go"));
        await loop.WhenIdleAsync();
        loop.Followup(Harness.Prompt("again"));
        await loop.WhenIdleAsync();

        // Without the reset, the third probe after the user prompt would be consecutive #3.
        Assert.Equal(0, Harness.Reminders(agent).Count, "a user prompt must reset the chain across turns");
    }

    public static async Task ChainsAreKeyedPerAgent()
    {
        await using var h = Harness.Create();
        h.Llm.RegisterAdapter(new[] { "mock-a" }, new ScriptedLlmAdapter(
            ScriptedResponse.Tool("a1", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("a2", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done")));
        h.Llm.RegisterAdapter(new[] { "mock-b" }, new ScriptedLlmAdapter(
            ScriptedResponse.Tool("b1", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("b2", "probe", "{\"q\":1}"),
            ScriptedResponse.Tool("b3", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done")));
        var (_, agentA, loopA) = h.CreateAgent("a", "mock-a");
        var (_, agentB, loopB) = h.CreateAgent("b", "mock-b");
        loopA.Followup(Harness.Prompt("go"));
        loopB.Followup(Harness.Prompt("go"));
        await Task.WhenAll(loopA.WhenIdleAsync(), loopB.WhenIdleAsync());

        Assert.Equal(0, Harness.Reminders(agentA).Count, "agent A's two repeats must not trip, despite B's three in the same registry");
        Assert.Equal(1, Harness.Reminders(agentB).Count, "agent B's three repeats must trip its own chain");
    }

    public static async Task FreshAgentStartsWithAFreshChain()
    {
        await using var h = Harness.Create(new RepeatToolReminderConfig { Thresholds = new[] { 2 } });
        h.RegisterAdapter(
            ScriptedResponse.Tool("c1", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done"),
            ScriptedResponse.Tool("c2", "probe", "{\"q\":1}"),
            ScriptedResponse.TextResponse("done"));
        var (firstHandle, _, firstLoop) = h.CreateAgent("reused");
        firstLoop.Followup(Harness.Prompt("go"));
        await firstLoop.WhenIdleAsync();
        firstHandle.Dispose();
        // The store keeps the session after the agent detaches; release the identity and the
        // guard drops the disposed session's chain.
        h.Sessions.Remove(new SessionId("reused"));

        var (_, second, secondLoop) = h.CreateAgent("reused");
        secondLoop.Followup(Harness.Prompt("go"));
        await secondLoop.WhenIdleAsync();

        Assert.Equal(0, Harness.Reminders(second).Count, "a fresh agent reusing the id must start its chain at one, not two");
    }

    public static void ConfigValidationFailsLoud()
    {
        using var ctx = new Context();
        var error = Assert.Throws<ArgumentException>(
            () => new RepeatToolReminderGuard(ctx, new RepeatToolReminderConfig { Thresholds = Array.Empty<int>() }),
            "an empty thresholds list must be refused");
        Assert.Contains("must not be empty", error.Message, "the empty-threshold refusal must state the rule");
        Assert.Null(ctx.Get<RepeatToolReminderGuard>(RepeatToolReminderGuard.ServiceKey), "a refused config must not leave the guard installed");

        Assert.Throws<ArgumentException>(
            () => new RepeatToolReminderGuard(ctx, new RepeatToolReminderConfig { Thresholds = new[] { 1, 3 } }),
            "a threshold below 2 must be refused");
        Assert.Throws<ArgumentException>(
            () => new RepeatToolReminderGuard(ctx, new RepeatToolReminderConfig { Thresholds = new[] { 3, 3 } }),
            "duplicate thresholds must be refused");
        Assert.Throws<ArgumentException>(
            () => new RepeatToolReminderGuard(ctx, new RepeatToolReminderConfig { ArgumentsPreviewChars = 0 }),
            "a non-positive argumentsPreviewChars must be refused");
    }
}
