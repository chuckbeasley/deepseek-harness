using Dsh.Sdk.Protocol;

namespace Dsh.Sdk.Protocol.Tests;

/// <summary>The wire-type contract: the method names, the handshake/request records, and the notification payloads.</summary>
public static class SdkProtocolTypesTests
{
    public static void TheMethodNamesAndServerIdentity_AreWireStable()
    {
        Assert.Equal("deepseek-harness-sdk-runtime", SdkProtocol.ServerName, "the server identity is wire-stable");
        Assert.Equal("initialize", SdkProtocol.Initialize, "the handshake method");
        Assert.Equal("session/prompt", SdkProtocol.SessionPrompt, "the prompt method");
        Assert.Equal("shutdown", SdkProtocol.Shutdown, "the shutdown method");
        Assert.Equal("session.event", SdkProtocol.SessionEvent, "the event notification");
        Assert.Equal("session.status", SdkProtocol.SessionStatus, "the status notification");
        Assert.Equal("subagent.started", SdkProtocol.SubagentStarted, "the subagent-started notification");
        Assert.Equal("subagent.finished", SdkProtocol.SubagentFinished, "the subagent-finished notification");
    }

    public static void TheRequestAndResultRecords_CarryTheirFields()
    {
        var initialize = new InitializeParams("C:\\work", "mock", "mock-todo", ReasoningEffort: "low", MaxTokens: 2048);
        Assert.Equal("C:\\work", initialize.Cwd, "the cwd rides along");
        Assert.Equal("mock", initialize.Provider, "the provider rides along");
        Assert.Equal("mock-todo", initialize.Model, "the model rides along");
        Assert.Equal("low", initialize.ReasoningEffort, "the reasoning effort rides along");
        Assert.Equal(2048, initialize.MaxTokens, "the token cap rides along");

        var result = new InitializeResult(new ServerInfo(SdkProtocol.ServerName, "0.1.0"));
        Assert.Equal(SdkProtocol.ServerName, result.Info.Name, "the server name");
        Assert.Equal("0.1.0", result.Info.Version, "the server version");

        var prompt = new SessionPromptParams("session-1", new SdkPromptContentBlock[]
        {
            new SdkPromptContentBlock.Block(new Dsh.Llm.TextBlock("hello")),
            new SdkPromptContentBlock.Image(new SdkEncodedImageBlock("aGVsbG8=", "image/png")),
        });
        Assert.Equal("session-1", prompt.SessionId, "the session id rides along");
        Assert.Equal(2, prompt.ContentBlocks.Count, "both block kinds compose");
        var receipt = new SessionPromptResult("evt-0");
        Assert.Equal("evt-0", receipt.MessageId, "the message id rides along");
    }

    public static void TheNotificationRecords_CarryTheirFields()
    {
        var sessionEvent = new SessionEventNotification("session-1",
            new WireSessionEvent("turn/start", 0, 0, System.Text.Json.JsonSerializer.SerializeToElement(new { turn = 1L })));
        Assert.Equal("session-1", sessionEvent.SessionId, "the session id rides along");
        Assert.Equal("turn/start", sessionEvent.Event.Type, "the event envelope rides along");
        Assert.Equal(1L, sessionEvent.Event.Data.GetProperty("turn").GetInt64(), "the payload rides along");

        var status = new SessionStatusNotification("session-1", "running");
        Assert.Equal("running", status.Status, "the lifecycle state rides along");

        var started = new SubagentStartedNotification("parent", "child");
        Assert.Equal("parent", started.ParentSessionId, "the parent rides along");
        Assert.Equal("child", started.ChildSessionId, "the child rides along");

        var finished = new SubagentFinishedNotification(
            "in-process", "child", "parent", "child", "ok", Dsh.Subagent.SubagentStopReason.Completed);
        Assert.Equal("ok", finished.Status, "the outcome rides along");
        Assert.Equal(Dsh.Subagent.SubagentStopReason.Completed, finished.StopReason, "the stop reason rides along");
        Assert.Null(finished.LastAssistantMessage, "absent assistant output stays absent");
    }
}
