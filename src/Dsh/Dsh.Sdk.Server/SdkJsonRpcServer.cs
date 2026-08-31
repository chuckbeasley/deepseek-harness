using System.Text.Json;
using Cordis.Core;
using Dsh.Sdk.Protocol;
using Dsh.Session;

namespace Dsh.Sdk.Server;

/// <summary>
/// The SDK runtime server (port of the TS <c>HarnessSdkJsonRpcServer</c>): hosts one booted
/// harness context over a JSON-RPC transport â€” the <c>initialize</c> handshake validates and
/// records the SDK route (mounting the DeepSeek adapter for an unowned <c>deepseek-official</c>
/// route), <c>session/prompt</c> lazily creates the agent+session pair on the ported agent loop
/// and enqueues the user message, and <c>shutdown</c> disposes the server-owned sessions and
/// subscriptions while the surrounding context keeps running. <c>session.event</c> and
/// <c>session.status</c> stream live. Documented reductions: the <c>subagent.started</c>/
/// <c>subagent.finished</c> notifications await the port's subagent lifecycle events and parent
/// lineage (the session header carries none), and inline image prompt blocks are rejected until
/// the attachment seam admits base64.
/// </summary>
public sealed class SdkJsonRpcServer
{
    private readonly Context _ctx;
    private readonly JsonRpcLineTransport _transport;
    private readonly List<IDisposable> _disposers = new();
    private readonly Dictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<SessionRecord>> _sessionCreations = new(StringComparer.Ordinal);
    private string _cwd = Environment.CurrentDirectory;
    private string _provider = "";
    private string _model = "";
    private int? _maxTokens;
    private IDisposable? _adapterRegistration;
    private bool _initialized;
    private bool _shuttingDown;
    private Task<JsonElement?>? _shutdownTask;

    /// <summary>The wire serializer: camel-cased, with the session log's polymorphic codecs and the prompt-block codec.</summary>
    private static readonly JsonSerializerOptions WireJson = CreateWireJson();

    private static JsonSerializerOptions CreateWireJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = Dsh.Session.SessionEventTypes.CreateSerializerOptions().TypeInfoResolver,
        };
        options.Converters.Add(new SdkPromptContentBlockConverter());
        return options;
    }

    private sealed class SessionRecord
    {
        public required Dsh.Agent.AgentHandle Handle { get; init; }
        public required Dsh.AgentLoop.LoopAgent Driver { get; init; }
    }

    /// <summary>
    /// Create the server over one booted context and transport. The constructor subscribes the
    /// session/status notifications and wires the transport's request handler; reinitialization
    /// is unsupported.
    /// </summary>
    public SdkJsonRpcServer(Context ctx, JsonRpcLineTransport transport)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        transport.OnRequest(HandleRequestAsync);
        _disposers.Add(ctx.On("session/event",
            new Action<Dsh.Session.Session, Dsh.Session.SessionEvent>((session, evt) =>
                transport.Notify(SdkProtocol.SessionEvent,
                    JsonSerializer.SerializeToElement(new SessionEventNotification(session.Id.Value, WireEnvelope(evt)), WireJson)))));
        _disposers.Add(ctx.On("agent/status",
            new Action<Dsh.Agent.AgentStatusPayload>(payload =>
                transport.Notify(SdkProtocol.SessionStatus,
                    JsonSerializer.SerializeToElement(new SessionStatusNotification(
                        payload.Agent.Session.Id.Value,
                        payload.Status == Dsh.Agent.AgentStatus.Running ? "running" : "idle"), WireJson)))));
    }

    /// <summary>
    /// Validate and record the SDK route. A provider with no registered adapter mounts the
    /// DeepSeek adapter when it is the <c>deepseek-official</c> route, then fails loud otherwise.
    /// </summary>
    public async Task<JsonElement?> InitializeAsync(JsonElement parameters)
    {
        var wire = parameters.Deserialize<InitializeParams>(WireJson)
            ?? throw new InvalidOperationException("invalid initialize parameters");
        if (wire.ReasoningEffort is { Length: 0 })
        {
            throw new InvalidOperationException("initialize reasoningEffort must be a non-empty string");
        }
        if (wire.MaxTokens is <= 0)
        {
            throw new InvalidOperationException("initialize maxTokens must be a positive integer");
        }
        var cwd = Path.GetFullPath(wire.Cwd ?? throw new InvalidOperationException("initialize cwd must be a string"));
        var provider = wire.Provider ?? throw new InvalidOperationException("initialize provider must be a string");
        var model = wire.Model ?? throw new InvalidOperationException("initialize model must be a string");
        var llm = _ctx.Get<Dsh.Llm.LlmRuntime>("llm")
            ?? throw new InvalidOperationException("SDK initialize requires the llm row");
        if (!llm.ListProviders().Contains(provider, StringComparer.Ordinal))
        {
            if (provider != "deepseek-official")
            {
                throw new InvalidOperationException($"no adapter registered for provider \"{provider}\"");
            }
            // The adapter reads its key/base-url from the environment at call time, like the
            // spine's deepseek row; mounting here makes an unowned official route usable.
            _adapterRegistration = llm.RegisterAdapter(new[] { provider }, new Dsh.Llm.DeepSeek.DeepSeekAdapter(
                new Dsh.Llm.DeepSeek.DeepSeekConfig
                {
                    ApiKey = Environment.GetEnvironmentVariable(Dsh.Llm.DeepSeek.DeepSeekAdapter.ApiKeyEnvVar),
                    BaseUrl = Environment.GetEnvironmentVariable(Dsh.Llm.DeepSeek.DeepSeekAdapter.BaseUrlEnvVar),
                }));
        }
        _cwd = cwd;
        _provider = provider;
        _model = model;
        // The port's AgentOptions has no reasoning-effort seat (the loop's call config does;
        // wiring it through the options is a loop-seam change, documented); maxTokens flows.
        _maxTokens = wire.MaxTokens;
        _initialized = true;
        return JsonSerializer.SerializeToElement(
            new InitializeResult(new ServerInfo(SdkProtocol.ServerName, "0.0.1")), WireJson);
    }

    /// <summary>
    /// Queue one identified prompt, lazily creating the agent+session pair on the recorded route.
    /// Inline image blocks are rejected until the attachment seam admits base64 (documented
    /// reduction); the message id is the durable user-message identity.
    /// </summary>
    public async Task<JsonElement?> PromptAsync(JsonElement parameters)
    {
        if (!_initialized) throw new InvalidOperationException("SDK server is not initialized");
        var wire = parameters.Deserialize<SessionPromptParams>(WireJson)
            ?? throw new InvalidOperationException("invalid session/prompt parameters");
        var record = await GetOrCreateSessionAsync(wire.SessionId);
        AssertLiveAgent(record, wire.SessionId);
        var content = DurablePromptContent(wire.ContentBlocks);
        AssertLiveAgent(record, wire.SessionId);
        var message = new Dsh.Llm.UserMessage
        {
            Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("D")),
            Content = content.ToArray(),
            Source = new Dsh.Llm.UserSource(),
        };
        record.Driver.Send(message, Dsh.Agent.InboxTarget.NextTurn, wakeup: true);
        return JsonSerializer.SerializeToElement(new SessionPromptResult(message.Id.Value), WireJson);
    }

    /// <summary>
    /// Project one ported session record to the SDK wire envelope: the discriminator and ordering
    /// fields on the envelope, every remaining payload field under <c>data</c> (the TS
    /// <c>SessionEvent</c> shape both SDK clients read).
    /// </summary>
    private static WireSessionEvent WireEnvelope(Dsh.Session.SessionEvent evt)
    {
        var json = JsonSerializer.SerializeToElement(evt, WireJson);
        var data = new Dictionary<string, JsonElement>();
        foreach (var property in json.EnumerateObject())
        {
            if (property.Name is "id" or "seq" or "timeMs" or "type" or "$type") continue;
            data[property.Name] = property.Value.Clone();
        }
        return new WireSessionEvent(evt.Type, evt.Seq, evt.TimeMs, JsonSerializer.SerializeToElement(data, WireJson));
    }

    /// <summary>Dispose the server-owned sessions, adapter, and subscriptions to quiescence; the surrounding context keeps running.</summary>
    public Task<JsonElement?> ShutdownAsync()
    {
        _shutdownTask ??= PerformShutdownAsync();
        return _shutdownTask;
    }

    /// <summary>Dispatch one incoming request; an unknown method throws (â†’ the transport's <c>-32603</c>).</summary>
    public Task<JsonElement?> HandleRequestAsync(string method, JsonElement? parameters)
        => method switch
        {
            SdkProtocol.Initialize => InitializeAsync(parameters ?? JsonSerializer.SerializeToElement(new { })),
            SdkProtocol.SessionPrompt => PromptAsync(parameters
                ?? throw new InvalidOperationException("session/prompt requires parameters")),
            SdkProtocol.Shutdown => ShutdownAsync(),
            _ => throw new InvalidOperationException($"unknown DeepSeek Harness SDK runtime method: {method}"),
        };

    private void AssertLiveAgent(SessionRecord record, string sessionId)
    {
        var agents = _ctx.Get<Dsh.Agent.AgentRegistry>("agents");
        if (agents is null || agents.Get(record.Handle.Agent.Id) != record.Handle.Agent)
        {
            throw new InvalidOperationException($"session agent was disposed outside the server: {sessionId}");
        }
    }

    private static IReadOnlyList<Dsh.Llm.ContentBlock> DurablePromptContent(IReadOnlyList<SdkPromptContentBlock> blocks)
    {
        var content = new List<Dsh.Llm.ContentBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            switch (block)
            {
                case SdkPromptContentBlock.Block(var durable):
                    content.Add(durable);
                    break;
                case SdkPromptContentBlock.Image:
                    // The port's attachment seam ingests from paths; base64 admission and raster
                    // validation are documented reductions, so inline images cannot be admitted.
                    throw new InvalidOperationException(
                        "SDK image prompt requires base64 attachment admission (not ported)");
            }
        }
        return content;
    }

    private async Task<SessionRecord> GetOrCreateSessionAsync(string sessionId)
    {
        if (_shuttingDown) throw new InvalidOperationException("SDK server is shutting down");
        if (_sessions.TryGetValue(sessionId, out var existing)) return existing;
        if (_sessionCreations.TryGetValue(sessionId, out var pending)) return await pending;
        var creation = CreateSessionAsync(sessionId);
        _sessionCreations[sessionId] = creation;
        try
        {
            var record = await creation;
            _sessions[sessionId] = record;
            return record;
        }
        finally
        {
            _sessionCreations.Remove(sessionId);
        }
    }

    private Task<SessionRecord> CreateSessionAsync(string sessionId)
    {
        var loop = _ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
            ?? throw new InvalidOperationException("SDK session creation requires the agentLoop row");
        var id = new SessionId(sessionId);
        var handle = loop.Create(id, new Dsh.Agent.AgentOptions
        {
            Provider = _provider,
            Model = _model,
            MaxTokens = _maxTokens,
        });
        var driver = loop.GetLoop(id)
            ?? throw new InvalidOperationException("the loop published no driver");
        return Task.FromResult(new SessionRecord { Handle = handle, Driver = driver });
    }

    private async Task<JsonElement?> PerformShutdownAsync()
    {
        _shuttingDown = true;
        foreach (var disposer in _disposers) disposer.Dispose();
        _disposers.Clear();
        foreach (var record in _sessions.Values) record.Handle.Dispose();
        _sessions.Clear();
        _adapterRegistration?.Dispose();
        _adapterRegistration = null;
        await Task.CompletedTask;
        return JsonSerializer.SerializeToElement(new { }, WireJson);
    }
}

