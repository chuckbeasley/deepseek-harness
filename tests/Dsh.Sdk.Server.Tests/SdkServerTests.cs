using System.IO.Pipelines;
using System.Text.Json;
using Cordis.Core;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Llm;
using Dsh.Sdk.Protocol;
using Dsh.Sdk.Server;
using Dsh.Session;
using Dsh.Session.Persistence;
using Dsh.Spike;
using Dsh.SystemPrompt;
using Dsh.Todo;
using Dsh.Tools;

namespace Dsh.Sdk.Server.Tests;

/// <summary>
/// The SDK runtime server over a real transport: the handshake validation and route recording,
/// lazy session creation and turns on the ported agent loop, the live notifications, the image
/// admission reduction, and shutdown semantics.
/// </summary>
public static class SdkServerTests
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Initialize_ReturnsTheServerIdentity_AndRecordsTheRoute()
    {
        using var harness = Harness.Create();
        try
        {
            var result = harness.Client.RequestAsync(SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = Environment.CurrentDirectory, provider = MockLlmProvider.Provider, model = MockLlmProvider.Model }, Wire)).GetAwaiter().GetResult();
            Assert.Equal(SdkProtocol.ServerName, result!.Value.GetProperty("serverInfo").GetProperty("name").GetString(), "the wire-stable server name");
            Assert.Equal("0.0.1", result.Value.GetProperty("serverInfo").GetProperty("version").GetString(), "the server version");
            var sessionId = "session-init";
            var prompt = harness.Client.RequestAsync(SdkProtocol.SessionPrompt,
                JsonSerializer.SerializeToElement(new { sessionId, contentBlocks = new object[] { new { type = "text", text = "hello" } } }, Wire)).GetAwaiter().GetResult();
            Assert.NotNull(prompt!.Value.GetProperty("messageId").GetString(), "the prompt returns the durable message id");
            Assert.True(harness.Loop.GetLoop(new SessionId(sessionId)) is not null, "the lazy session was created on the loop");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void Initialize_RejectsMalformedParameters()
    {
        using var harness = Harness.Create();
        try
        {
            var badEffort = RequestError(harness, SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = ".", provider = "mock", model = "mock-todo", reasoningEffort = "" }, Wire));
            Assert.True(badEffort.Contains("reasoningEffort", StringComparison.Ordinal), "the empty reasoning effort names the field");

            var badTokens = RequestError(harness, SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = ".", provider = "mock", model = "mock-todo", maxTokens = 0 }, Wire));
            Assert.True(badTokens.Contains("maxTokens", StringComparison.Ordinal), "the non-positive cap names the field");

            var unknown = RequestError(harness, SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = ".", provider = "no-such-provider", model = "x" }, Wire));
            Assert.True(unknown.Contains("no adapter registered", StringComparison.Ordinal), "an unknown provider fails loud");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void DeepseekOfficial_MountsTheFallbackAdapter()
    {
        using var harness = Harness.Create();
        try
        {
            var result = harness.Client.RequestAsync(SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = ".", provider = "deepseek-official", model = "deepseek-chat" }, Wire)).GetAwaiter().GetResult();
            Assert.Equal("deepseek-harness-sdk-runtime", result!.Value.GetProperty("serverInfo").GetProperty("name").GetString(), "the handshake succeeds");
            Assert.True(harness.Llm.ListProviders().Contains("deepseek-official"), "the official route mounted the fallback adapter");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void Prompt_WithoutInitialize_Fails()
    {
        using var harness = Harness.Create();
        try
        {
            var message = RequestError(harness, SdkProtocol.SessionPrompt,
                JsonSerializer.SerializeToElement(new { sessionId = "s", contentBlocks = new object[] { } }, Wire));
            Assert.True(message.Contains("not initialized", StringComparison.Ordinal), "a prompt before the handshake is refused");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void ATurn_RunsAndStreamsTheLiveNotifications()
    {
        using var harness = Harness.Create();
        try
        {
            var notifications = new List<(string Method, JsonElement Params)>();
            harness.Client.OnNotification((method, parameters) => notifications.Add((method, parameters!.Value)));
            harness.Client.RequestAsync(SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = Environment.CurrentDirectory, provider = MockLlmProvider.Provider, model = MockLlmProvider.Model }, Wire)).GetAwaiter().GetResult();
            var sessionId = "session-notify";
            harness.Client.RequestAsync(SdkProtocol.SessionPrompt,
                JsonSerializer.SerializeToElement(new { sessionId, contentBlocks = new object[] { new { type = "text", text = "plan the round" } } }, Wire)).GetAwaiter().GetResult();
            var session = harness.Sessions.Get(new SessionId(sessionId))!;
            Assert.WaitUntil(() => session.Events.Any(evt => evt is AssistantMessageEvent));
            Assert.True(notifications.Any(n => n.Method == SdkProtocol.SessionEvent), "session.event frames stream");
            Assert.True(notifications.Any(n => n.Method == SdkProtocol.SessionStatus), "session.status frames stream");
            Assert.True(notifications.Any(n => n.Method == SdkProtocol.SessionStatus
                && n.Params.GetProperty("status").GetString() == "running"), "the running transition was announced");
            Assert.True(notifications.Any(n => n.Method == SdkProtocol.SessionEvent
                && n.Params.GetProperty("event").GetProperty("type").GetString() == "turn/start"), "the turn lifecycle streams");
            Assert.True(notifications.Any(n => n.Method == SdkProtocol.SessionEvent
                && n.Params.GetProperty("event").TryGetProperty("data", out var data)
                && data.ValueKind == System.Text.Json.JsonValueKind.Object),
                "the event envelope carries the variant payload under data");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void ImageBlocks_AreRejectedUntilAdmissionIsPorted()
    {
        using var harness = Harness.Create();
        try
        {
            harness.Client.RequestAsync(SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = ".", provider = MockLlmProvider.Provider, model = MockLlmProvider.Model }, Wire)).GetAwaiter().GetResult();
            var message = RequestError(harness, SdkProtocol.SessionPrompt,
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = "session-image",
                    contentBlocks = new object[] { new { type = "image", data = "aGVsbG8=", mimeType = "image/png" } },
                }, Wire));
            Assert.True(message.Contains("base64 attachment admission", StringComparison.Ordinal),
                "the image prompt names the missing admission surface");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void Shutdown_DisposesSessions_AndFurtherPromptsFail()
    {
        using var harness = Harness.Create();
        try
        {
            harness.Client.RequestAsync(SdkProtocol.Initialize,
                JsonSerializer.SerializeToElement(new { cwd = ".", provider = MockLlmProvider.Provider, model = MockLlmProvider.Model }, Wire)).GetAwaiter().GetResult();
            const string sessionId = "session-shutdown";
            harness.Client.RequestAsync(SdkProtocol.SessionPrompt,
                JsonSerializer.SerializeToElement(new { sessionId, contentBlocks = new object[] { new { type = "text", text = "hi" } } }, Wire)).GetAwaiter().GetResult();
            var shutdown = harness.Client.RequestAsync(SdkProtocol.Shutdown, null).GetAwaiter().GetResult();
            Assert.Equal(JsonValueKind.Object, shutdown!.Value.ValueKind, "shutdown answers an empty result");
            Assert.Null(harness.Loop.GetLoop(new SessionId(sessionId)), "the server-owned session was disposed");
            var after = RequestError(harness, SdkProtocol.SessionPrompt,
                JsonSerializer.SerializeToElement(new { sessionId, contentBlocks = new object[] { } }, Wire));
            Assert.True(after.Contains("shutting down", StringComparison.Ordinal)
                || after.Contains("disposed", StringComparison.Ordinal),
                "a prompt after shutdown fails");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void UnknownMethod_AnswersTheTransportError()
    {
        using var harness = Harness.Create();
        try
        {
            var error = Assert.ThrowsAny<JsonRpcResponseError>(
                () => { harness.Client.RequestAsync("no-such-method", new { }).GetAwaiter().GetResult(); return Task.CompletedTask; },
                "an unknown method must surface as an error response");
            Assert.Equal(-32603, error.Code, "the transport's internal-error code");
            Assert.True(error.Message.Contains("unknown DeepSeek Harness SDK runtime method", StringComparison.Ordinal), "the failure names the rule");
        }
        finally
        {
            harness.Dispose();
        }
    }

    /// <summary>Send one request and return the JSON-RPC error message; a success is a test failure.</summary>
    private static string RequestError(Harness harness, string method, object? parameters)
    {
        try
        {
            harness.Client.RequestAsync(method, parameters).GetAwaiter().GetResult();
        }
        catch (JsonRpcResponseError error)
        {
            return error.Message;
        }
        throw new AssertionException($"expected an error response for {method}");
    }
    private sealed class Harness : IDisposable
    {
        public required Context Ctx { get; init; }
        public required LlmRuntime Llm { get; init; }
        public required SessionStore Sessions { get; init; }
        public required Dsh.AgentLoop.AgentLoop Loop { get; init; }
        public required JsonRpcLineTransport Client { get; init; }
        public required JsonRpcLineTransport Server { get; init; }
        private readonly Pipe[] _pipes;

        private Harness(Pipe[] pipes)
        {
            _pipes = pipes;
        }

        public static Harness Create()
        {
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var llm = new LlmRuntime(ctx);
            var tools = new ToolRuntime(ctx);
            _ = new SystemPromptService(ctx);
            var agents = new AgentRegistry(ctx);
            var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-sdk-server-tests-" + Guid.NewGuid().ToString("N"));
            var persistence = new SessionPersistenceService(ctx, new PersistenceConfig { Root = tempRoot });
            _ = persistence.Attach(sessions);
            llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, new MockLlmProvider());
            _ = new TodoService(ctx, allowParallelInProgress: false);
            tools.Register(TodoTool.Definition(ctx, allowParallelInProgress: false));
            var loop = new Dsh.AgentLoop.AgentLoop(ctx);
            var pipes = new[] { new Pipe(), new Pipe() };
            var serverTransport = new JsonRpcLineTransport(pipes[0].Reader.AsStream(), pipes[1].Writer.AsStream());
            var clientTransport = new JsonRpcLineTransport(pipes[1].Reader.AsStream(), pipes[0].Writer.AsStream());
            _ = new SdkJsonRpcServer(ctx, serverTransport);
            serverTransport.Start();
            clientTransport.Start();
            _ = agents;
            return new Harness(pipes)
            {
                Ctx = ctx,
                Llm = llm,
                Sessions = sessions,
                Loop = loop,
                Client = clientTransport,
                Server = serverTransport,
            };
        }

        public void Dispose()
        {
            Client.Close();
            Server.Close();
            foreach (var pipe in _pipes)
            {
                pipe.Writer.Complete();
                pipe.Reader.Complete();
            }
            Ctx.Dispose();
        }
    }
}


