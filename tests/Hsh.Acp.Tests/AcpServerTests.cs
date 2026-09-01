using System.Text.Json;
using Harness.Acp;
using Harness.Agent;
using Harness.Llm;
using Harness.Sdk.Client;
using Harness.Sdk.Protocol;
using Harness.Session;
using Harness.Spike;

namespace Harness.Acp.Tests;

/// <summary>Pure codec and model-control tests (ports of the TS codec and model-control specs).</summary>
public static class AcpCodecTests
{
    public static void TurnEndToStopReason_MapsTheVocabulary()
    {
        Assert.Equal("end_turn", AcpCodec.TurnEndToStopReason(new CompletedReason()), "completion reports end_turn");
        Assert.Equal("max_tokens", AcpCodec.TurnEndToStopReason(new MaxTokensReason()), "the token ceiling reports max_tokens");
        Assert.Equal("cancelled", AcpCodec.TurnEndToStopReason(new InterruptedReason()), "a crash-orphaned turn reports cancelled");
        Assert.Equal("end_turn", AcpCodec.TurnEndToStopReason(new AbortedReason(new UserCancel())), "an abort by another owner is ordinary quiescence");
        Assert.Equal("end_turn", AcpCodec.TurnEndToStopReason(new BlockedReason()), "a blocked turn reports end_turn");
        Assert.Equal("end_turn", AcpCodec.TurnEndToStopReason(new ErrorReason(new LlmFailure("boom", "ERR"))), "an error turn reports end_turn");
    }

    public static void ModelControl_AdvertisesTheFixedRoute()
    {
        var control = new AcpModelControl(MockLlmProvider.Provider, MockLlmProvider.Model);
        var options = control.Options();
        Assert.Equal(1, options.Count, "one standard option");
        Assert.Equal("model", options[0].Id, "the model option id");
        Assert.Equal("select", options[0].Type, "the select kind");
        var value = AcpModelControl.ModelValue(MockLlmProvider.Provider, MockLlmProvider.Model);
        Assert.Equal(value, options[0].CurrentValue, "the current route is the current value");
        Assert.Equal(1, options[0].Options.Count, "one provider group");
        Assert.Equal(MockLlmProvider.Provider, options[0].Options[0].Group, "the group names the provider");
        Assert.Equal(1, options[0].Options[0].Options.Count, "one model choice");
        Assert.Equal(MockLlmProvider.Model, options[0].Options[0].Options[0].Name, "the choice names the model");

        var accepted = control.Set("model", JsonSerializer.SerializeToElement(value));
        Assert.Equal(value, accepted[0].CurrentValue, "the current value round-trips");
        var error = Assert.ThrowsAny<AcpModelConfigError>(
            () => control.Set("model", JsonSerializer.SerializeToElement("[\"mock\",\"other\"]")),
            "an unknown model option is refused");
        Assert.Contains("unknown model option", error.Message, "the failure names the option");
        var unknown = Assert.ThrowsAny<AcpModelConfigError>(
            () => control.Set("reasoning_effort", JsonSerializer.SerializeToElement("x")),
            "an unknown option id is refused");
        Assert.Contains("unknown session config option", unknown.Message, "the failure names the id");
    }

    public static void ModelControl_WithoutARoute_AdvertisesNothing()
    {
        var control = new AcpModelControl(null, null);
        Assert.Equal(0, control.Options().Count, "no selection, no options");
    }
}

/// <summary>The ACP server over a real transport: identity, sessions, turns, updates, approval,
/// cancellation, the list/resume surface, the reductions, and the real profile end to end.</summary>
public static class AcpServerTests
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Initialize_ReturnsTheIdentityAndCapabilities()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var result = Request(harness, AcpProtocol.Initialize, null);
            Assert.Equal("2025-03-26", result!.GetProperty("protocolVersion").GetString(), "the wire protocol version");
            Assert.Equal("deepseek-harness-acp", result.GetProperty("agentInfo").GetProperty("name").GetString(), "the wire-stable agent name");
            Assert.Equal("0.0.1", result.GetProperty("agentInfo").GetProperty("version").GetString(), "the agent version");
            Assert.False(result.GetProperty("agentCapabilities").GetProperty("promptCapabilities").GetProperty("image").GetBoolean(),
                "image prompts are not advertised (the admission reduction)");
            Assert.Equal(JsonValueKind.Object, result.GetProperty("agentCapabilities").GetProperty("sessionCapabilities").GetProperty("resume").ValueKind,
                "the session capabilities are declared");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void NewSession_ValidatesAndCreatesTheSession()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var badCwd = RequestError(harness, AcpProtocol.SessionNew, new { cwd = "relative" });
            Assert.Contains("cwd must be an absolute path", badCwd, "a relative cwd is refused");

            var mcp = RequestError(harness, AcpProtocol.SessionNew,
                new { cwd = Environment.CurrentDirectory, mcpServers = new object[] { new { name = "x", command = "C:\\x" } } });
            Assert.Contains("MCP client seam", mcp, "MCP mounts name the reduction");

            var dirs = RequestError(harness, AcpProtocol.SessionNew,
                new { cwd = Environment.CurrentDirectory, additionalDirectories = new[] { "C:\\other" } });
            Assert.Contains("additionalDirectories is not supported", dirs, "extra directories are refused");

            var created = Request(harness, AcpProtocol.SessionNew, new { cwd = Environment.CurrentDirectory });
            var sessionId = created!.GetProperty("sessionId").GetString()!;
            Assert.True(sessionId.StartsWith("session-", StringComparison.Ordinal), "the session id is minted");
            var configOptions = created.GetProperty("configOptions");
            Assert.Equal(1, configOptions.GetArrayLength(), "the model option is advertised");
            Assert.NotNull(harness.Loop.GetLoop(new SessionId(sessionId)), "the session was created on the loop");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void ATurn_RunsAndStreamsTheCommittedUpdates()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var updates = new List<(string Kind, JsonElement Params)>();
            harness.Client.OnNotification((method, parameters) =>
            {
                if (method == AcpProtocol.ClientSessionUpdate)
                {
                    updates.Add(("update", parameters!.Value));
                }
            });
            harness.Client.OnRequest((method, parameters) =>
            {
                // The approval bridge asks one permission per tool call; allow it.
                if (method != AcpProtocol.ClientRequestPermission)
                {
                    throw new InvalidOperationException($"unexpected client request {method}");
                }
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    outcome = new { outcome = "performed", optionId = "allow-once" },
                }, Wire));
            });
            var sessionId = NewSession(harness);
            var result = Request(harness, AcpProtocol.SessionPrompt, new { sessionId, prompt = new object[] { new { type = "text", text = "plan the round" } } });
            Assert.Equal("end_turn", result!.GetProperty("stopReason").GetString(), "the mock turn completes");


            Assert.True(updates.Any(u => u.Params.GetProperty("update").GetProperty("sessionUpdate").GetString() == "tool_call"
                && u.Params.GetProperty("update").GetProperty("toolCallId").GetString() == MockLlmProvider.ToolCallIdValue),
                "the tool call lifecycle streams");
            Assert.True(updates.Any(u => u.Params.GetProperty("update").GetProperty("sessionUpdate").GetString() == "tool_call_update"),
                "the tool result lifecycle streams");
            Assert.True(updates.Any(u => u.Params.GetProperty("update").GetProperty("sessionUpdate").GetString() == "agent_message_chunk"
                && u.Params.GetProperty("update").GetProperty("content").GetProperty("text").GetString() == "Todo list recorded."),
                "the final assistant text streams as a message chunk");
            Assert.True(updates.All(u => u.Params.GetProperty("sessionId").GetString() == sessionId), "every update names the session");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void TheApprovalBridge_AsksOneShotDecisions()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var asked = new List<JsonElement>();
            harness.Client.OnRequest((method, parameters) =>
            {
                if (method != AcpProtocol.ClientRequestPermission)
                {
                    throw new InvalidOperationException($"unexpected client request {method}");
                }
                asked.Add(parameters!.Value.Clone());
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    outcome = new { outcome = "performed", optionId = "allow-once" },
                }, Wire));
            });
            var sessionId = NewSession(harness);
            _ = Request(harness, AcpProtocol.SessionPrompt, new { sessionId, prompt = new object[] { new { type = "text", text = "ask" } } });
            Assert.Equal(1, asked.Count, "the mock turn's tool call asks exactly once");
            Assert.Equal(MockLlmProvider.ToolCallIdValue, asked[0].GetProperty("toolCall").GetProperty("toolCallId").GetString(), "the tool call id rides along");
            var options = asked[0].GetProperty("options");
            Assert.Equal(2, options.GetArrayLength(), "one-shot allow and reject choices");
            Assert.Equal("allow-once", options[0].GetProperty("optionId").GetString(), "the allow option id");
            Assert.Equal("reject-once", options[1].GetProperty("optionId").GetString(), "the reject option id");

            var rejected = new List<JsonElement>();
            harness.Client.OnRequest((method, parameters) =>
            {
                if (method != AcpProtocol.ClientRequestPermission)
                {
                    throw new InvalidOperationException($"unexpected client request {method}");
                }
                rejected.Add(parameters!.Value.Clone());
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    outcome = new { outcome = "performed", optionId = "reject-once" },
                }, Wire));
            });
            var second = Request(harness, AcpProtocol.SessionPrompt, new { sessionId, prompt = new object[] { new { type = "text", text = "deny" } } });
            Assert.Equal("end_turn", second!.GetProperty("stopReason").GetString(), "a denied tool still completes the turn");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void Cancel_StopsTheActivePrompt()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(
            new AcpServerConfig(SlowAdapter.Provider, SlowAdapter.Model), SlowAdapter: new SlowAdapter()));
        try
        {
            var sessionId = NewSession(harness);
            var session = harness.Sessions.Get(new SessionId(sessionId))!;
            var promptTask = Task.Run(() => Request(harness, AcpProtocol.SessionPrompt,
                new { sessionId, prompt = new object[] { new { type = "text", text = "wait" } } }));
            Assert.WaitUntil(() => session.Events.Any(evt => evt is StepStartEvent), 15000);
            harness.Client.Notify(AcpProtocol.SessionCancel, JsonSerializer.SerializeToElement(new { sessionId }, Wire));
            var result = promptTask.GetAwaiter().GetResult();
            Assert.Equal("cancelled", result!.GetProperty("stopReason").GetString(), "the explicit cancel settles cancelled");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void CloseSession_DisposesTheAgent_AndFurtherPromptsFail()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var sessionId = NewSession(harness);
            var closed = Request(harness, AcpProtocol.SessionClose, new { sessionId });
            Assert.Equal(JsonValueKind.Object, closed!.ValueKind, "session/close answers an empty result");
            Assert.Null(harness.Loop.GetLoop(new SessionId(sessionId)), "the owned agent was disposed");
            var error = RequestError(harness, AcpProtocol.SessionPrompt,
                new { sessionId, prompt = new object[] { new { type = "text", text = "hi" } } });
            Assert.Contains("unknown session", error, "a closed session is unknown");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void ListSessions_PagesThePersistedSessions()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(new AcpServerConfig(MockLlmProvider.Provider, MockLlmProvider.Model, SessionListPageSize: 2)));
        try
        {
            var created = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                created.Add(NewSession(harness));
                _ = Request(harness, AcpProtocol.SessionPrompt, new { sessionId = created[^1], prompt = new object[] { new { type = "text", text = "run" } } });
                _ = Request(harness, AcpProtocol.SessionClose, new { sessionId = created[^1] });
            }
            var first = Request(harness, AcpProtocol.SessionList, new { });
            var sessions = first!.GetProperty("sessions");
            Assert.Equal(2, sessions.GetArrayLength(), "the first page holds the page size");
            var nextCursor = first.GetProperty("nextCursor").GetString();
            Assert.NotNull(nextCursor, "a second page exists");
            var second = Request(harness, AcpProtocol.SessionList, new { cursor = nextCursor });
            Assert.Equal(1, second!.GetProperty("sessions").GetArrayLength(), "the second page holds the remainder");
            Assert.False(second.GetProperty("sessions")[0].GetProperty("sessionId").GetString() == sessions[0].GetProperty("sessionId").GetString(),
                "the pages do not overlap");
            var bad = RequestError(harness, AcpProtocol.SessionList, new { cursor = "not-base64url!" });
            Assert.Contains("cursor is invalid", bad, "a malformed cursor is refused");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void ResumeSession_RestoresAPersistedSession()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var sessionId = NewSession(harness);
            _ = Request(harness, AcpProtocol.SessionPrompt, new { sessionId, prompt = new object[] { new { type = "text", text = "run" } } });
            _ = Request(harness, AcpProtocol.SessionClose, new { sessionId });
            var resumed = Request(harness, AcpProtocol.SessionResume, new { sessionId, cwd = Environment.CurrentDirectory });
            Assert.Equal(JsonValueKind.Array, resumed!.GetProperty("configOptions").ValueKind, "the resumed session advertises its options");
            Assert.NotNull(harness.Loop.GetLoop(new SessionId(sessionId)), "the resumed session is live");
            var result = Request(harness, AcpProtocol.SessionPrompt, new { sessionId, prompt = new object[] { new { type = "text", text = "again" } } });
            Assert.Equal("end_turn", result!.GetProperty("stopReason").GetString(), "the resumed session runs a turn");
            var missing = RequestError(harness, AcpProtocol.SessionResume, new { sessionId = "session-never", cwd = Environment.CurrentDirectory });
            Assert.Contains("not resumable", missing, "an unknown session is refused");
            var active = RequestError(harness, AcpProtocol.SessionResume, new { sessionId, cwd = Environment.CurrentDirectory });
            Assert.Contains("already active", active, "an active session is refused");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void TheReductions_RefuseImagesAndUnknownBlocks()
    {
        using var harness = Harness.Create(new AcpHarnessOptions(MockConfig()));
        try
        {
            var sessionId = NewSession(harness);
            var image = RequestError(harness, AcpProtocol.SessionPrompt, new
            {
                sessionId,
                prompt = new object[] { new { type = "image", data = "aGVsbG8=", mimeType = "image/png" } },
            });
            Assert.Contains("attachment admission", image, "image prompts name the reduction");
            var unknownBlock = RequestError(harness, AcpProtocol.SessionPrompt, new
            {
                sessionId,
                prompt = new object[] { new { type = "resource_link", name = "x", uri = "file:///x" } },
            });
            Assert.Contains("not supported", unknownBlock, "unknown block types are refused");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void TheAcpProfile_ServesARealClientOverStdio()
    {
        using var temp = new TempDir();
        using var client = new HarnessClient(null, Runtime.Resolve(temp.Path, temp.Path));
        try
        {
            client.Start();
            var initialize = client.RequestAsync(AcpProtocol.Initialize, null).GetAwaiter().GetResult();
            Assert.Equal("deepseek-harness-acp", initialize!.Value.GetProperty("agentInfo").GetProperty("name").GetString(),
                "the real acp profile answers the handshake");
            // The keyless profile route is the mock, whose first turn tool-calls; the client never
            // answers session/requestPermission (its transport has no request handler), so the
            // bridge's ask fails closed, the tool is denied, and the turn still completes.
            var sessionNew = client.RequestAsync(AcpProtocol.SessionNew,
                new { cwd = temp.Path, mcpServers = Array.Empty<object>() }).GetAwaiter().GetResult();
            var sessionId = sessionNew!.Value.GetProperty("sessionId").GetString()!;
            var prompt = client.RequestAsync(AcpProtocol.SessionPrompt, new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "plan the profile" } },
            }).GetAwaiter().GetResult();
            Assert.Equal("end_turn", prompt!.Value.GetProperty("stopReason").GetString(), "the profile runs the mock turn");
        }
        finally
        {
            client.CloseAsync().GetAwaiter().GetResult();
        }
    }

    private static string NewSession(Harness harness)
    {
        var created = Request(harness, AcpProtocol.SessionNew, new { cwd = Environment.CurrentDirectory });
        return created!.GetProperty("sessionId").GetString()!;
    }

    private static AcpServerConfig MockConfig() => new(MockLlmProvider.Provider, MockLlmProvider.Model);

    private static JsonElement Request(Harness harness, string method, object? parameters)
        => harness.Client.RequestAsync(method,
            parameters is null ? null : JsonSerializer.SerializeToElement(parameters, Wire)).GetAwaiter().GetResult()
            ?? throw new AssertionException($"expected a result for {method}");

    private static string RequestError(Harness harness, string method, object? parameters)
    {
        try
        {
            Request(harness, method, parameters);
        }
        catch (JsonRpcResponseError error)
        {
            return error.Message;
        }
        throw new AssertionException($"expected an error response for {method}");
    }

    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-acp-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // best-effort cleanup
            }
        }
    }
}
