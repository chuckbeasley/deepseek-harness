using System.Text.Json;
using Dsh.Sdk.Protocol;

namespace Dsh.Sdk.Client.Tests;

/// <summary>Pure wire-semantics tests (port of the TS client's helpers): prompt normalization,
/// event validation, the inbox receipt, final-response selection, and the lineage map.</summary>
public static class SemanticsTests
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void NormalizeInput_TurnsTextIntoOneTextBlock()
    {
        var blocks = SdkWire.NormalizeInput("hello");
        Assert.Equal(1, blocks.Count, "a string becomes one block");
        var wireOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        wireOptions.Converters.Add(new SdkPromptContentBlockConverter());
        var wire = JsonSerializer.SerializeToElement(blocks, wireOptions);
        var roundTrip = wire.Deserialize<IReadOnlyList<SdkPromptContentBlock>>(wireOptions);
        Assert.Equal(1, roundTrip.Count, "the wire round-trips one block");
        if (roundTrip[0] is not SdkPromptContentBlock.Block(var content) || content is not Dsh.Llm.TextBlock text)
        {
            throw new AssertionException("the round trip did not yield a text block");
        }
        Assert.Equal("hello", text.Text, "the text is verbatim");
    }

    public static void FinalResponse_SelectsTheLastAssistantMessageText()
    {
        var events = new List<WireSessionEvent>
        {
            Event("user/message", new { message = new { id = "m1" } }),
            Event("assistant/message", new { message = new { content = new object[]
                { new { type = "tool_use" }, new { type = "text", text = "first " } } } }),
            Event("todo/write", new { todos = new object[] { } }),
            Event("assistant/message", new { message = new { content = new object[]
                { new { type = "text", text = "final" }, new { type = "text", text = " answer" } } } }),
        };
        Assert.Equal("final answer", SdkWire.FinalResponse(events), "the last assistant message's text wins");
        Assert.Equal("", SdkWire.FinalResponse(new List<WireSessionEvent>()), "no assistant message yields an empty response");
    }

    public static void IsInboxReceipt_MatchesTheDurableUserMessage()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "user/message", seq = 1L, timeMs = 2L, data = new { message = new { id = "m-1" } } },
        }, Wire);
        Assert.True(SdkWire.IsInboxReceipt(parameters, "m-1"), "the durable user message is the receipt");
        Assert.False(SdkWire.IsInboxReceipt(parameters, "m-2"), "another message id is not the receipt");
        var other = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "assistant/message", seq = 2L, timeMs = 3L, data = new { message = new { id = "m-1" } } },
        }, Wire);
        Assert.False(SdkWire.IsInboxReceipt(other, "m-1"), "only the user message variant is the receipt");
    }

    public static void ValidatedSessionEvent_ValidatesTheReadVariants()
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "assistant/message", seq = 1L, timeMs = 2L,
                data = new { message = new { content = new object[] { new { type = "text", text = "hi" } } } } },
        }, Wire);
        var parsed = SdkWire.ValidatedSessionEvent(envelope);
        Assert.Equal("assistant/message", parsed.Type, "the discriminator survives");
        Assert.Equal(1L, parsed.Seq, "the ordering field survives");
        Assert.Equal(2L, parsed.TimeMs, "the timestamp survives");

        var malformed = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "assistant/message", seq = 1L, timeMs = 2L, data = new { message = new { } } },
        }, Wire);
        Assert.ThrowsAny<SdkProtocolError>(() => SdkWire.ValidatedSessionEvent(malformed),
            "an assistant message without content is a protocol error");

        var badReason = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "turn/end", seq = 3L, timeMs = 4L, data = new { } },
        }, Wire);
        Assert.ThrowsAny<SdkProtocolError>(() => SdkWire.ValidatedSessionEvent(badReason),
            "a turn end without a reason envelope is a protocol error");

        var unknownAbort = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "turn/end", seq = 3L, timeMs = 4L,
                data = new { reason = new { kind = "aborted", cause = new { type = "mystery" } } } },
        }, Wire);
        Assert.ThrowsAny<SdkProtocolError>(() => SdkWire.ValidatedSessionEvent(unknownAbort),
            "an unknown abort cause is a protocol error");

        var hookAbort = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "turn/end", seq = 3L, timeMs = 4L,
                data = new { reason = new { kind = "aborted", cause = new { type = "hook" } } } },
        }, Wire);
        Assert.ThrowsAny<SdkProtocolError>(() => SdkWire.ValidatedSessionEvent(hookAbort),
            "a hook abort without its reason is a protocol error");

        // Plugin event types unknown to the client process pass through under their envelope shape.
        var pluginEvent = JsonSerializer.SerializeToElement(new
        {
            sessionId = "s",
            @event = new { type = "todo/write", seq = 4L, timeMs = 5L, data = new { todos = new object[] { } } },
        }, Wire);
        var passed = SdkWire.ValidatedSessionEvent(pluginEvent);
        Assert.Equal("todo/write", passed.Type, "an unknown plugin event passes through");
    }

    public static void SessionLineage_TracksDescendantChains()
    {
        var lineage = new SessionLineage();
        lineage.Record(Started("session-parent", "session-child"));
        lineage.Record(Started("session-child", "session-grandchild"));
        Assert.True(lineage.IsDescendantOf("session-grandchild", "session-parent"), "a grandchild walks to the root");
        Assert.True(lineage.IsDescendantOf("session-child", "session-parent"), "a child walks to the root");
        Assert.True(lineage.IsDescendantOf("session-parent", "session-parent"), "the root matches itself");
        Assert.False(lineage.IsDescendantOf("session-other", "session-parent"), "an unrelated session is not a descendant");
        Assert.False(lineage.IsDescendantOf("session-grandchild", "session-other"), "the walk does not cross trees");
        lineage.Record(Started("", "session-orphan"));
        Assert.False(lineage.IsDescendantOf("session-orphan", "session-parent"), "an empty parent edge is ignored");
    }

    private static WireSessionEvent Event(string type, object data)
        => new(type, 0, 0, JsonSerializer.SerializeToElement(data, Wire));

    private static HarnessNotification Started(string parentId, string childId)
        => new(SdkProtocol.SubagentStarted,
            JsonSerializer.SerializeToElement(new { parentSessionId = parentId, childSessionId = childId }, Wire));
}
