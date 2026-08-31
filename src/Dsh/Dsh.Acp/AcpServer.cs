using System.Text;
using System.Text.Json;
using Cordis.Core;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Sdk.Protocol;
using Dsh.Session;
using Dsh.Session.Persistence;
using Dsh.Tools;

namespace Dsh.Acp;

/// <summary>Deployment configuration for the ACP server.</summary>
public sealed record AcpServerConfig(
    /// <summary>Provider route for created agents; <c>null</c> uses the loop defaults.</summary>
    string? Provider = null,
    /// <summary>Model id for created agents; <c>null</c> uses the loop defaults.</summary>
    string? Model = null,
    /// <summary>Maximum summaries returned by one session/list page.</summary>
    int SessionListPageSize = 100);

/// <summary>
/// The automation-only ACP server (port of the TS <c>@deepseek-ai/dsh-acp</c>): exposes
/// persistent harness sessions to trusted programmatic clients over the standard Agent Client
/// Protocol on one JSON-RPC transport. It carries standard configuration, prompt content,
/// committed semantic updates, cancellation, and one-shot permission decisions; presentation and
/// human-interaction features stay with the harness's UI modules. Documented reductions: MCP
/// mounts are refused until the port has an MCP client seam, inline image prompts await the
/// attachment admission seam, the model option advertises the session's fixed route only (the
/// loop reads AgentOptions at creation), and usage updates await the port's token meter.
/// </summary>
public sealed class AcpServer
{
    private readonly Context _ctx;
    private readonly JsonRpcLineTransport _transport;
    private readonly AcpServerConfig _config;
    private readonly Dsh.AgentLoop.AgentLoop _loop;
    private readonly SessionPersistenceService? _persistence;
    private readonly Dictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _disposers = new();
    private bool _closed;
    private Task? _shutdownTask;

    /// <summary>
    /// Create the server over one booted context and transport. The constructor subscribes the
    /// runtime events and the interaction waterfall; reinitialization is unsupported.
    /// </summary>
    public AcpServer(Context ctx, JsonRpcLineTransport transport, AcpServerConfig? config = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _config = config ?? new AcpServerConfig();
        if (_config.SessionListPageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "sessionListPageSize must be a positive integer");
        }
        _loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
            ?? throw new InvalidOperationException("acp requires the agentLoop row");
        _persistence = ctx.Get<SessionPersistenceService>("sessionPersistence");
        transport.OnRequest(HandleRequestAsync);
        transport.OnNotification(HandleNotificationAsync);
        _disposers.Add(ctx.On("session/event",
            new Action<Dsh.Session.Session, SessionEvent>((session, evt) =>
            {
                if (_sessions.TryGetValue(session.Id.Value, out var record) && record.OwnsSession(session))
                {
                    record.OnSessionEvent(session, evt);
                }
            })));
        _disposers.Add(ctx.On("agent/inbox/claimed",
            new Action<AgentInboxClaimedPayload>(payload =>
            {
                var record = OwnedRecord(payload.Agent);
                record?.OnInboxClaimed(payload.Message, payload.Turn);
            })));
        _disposers.Add(ctx.On("agent/error",
            new Action<AgentErrorPayload>(payload =>
            {
                var record = OwnedRecord(payload.Agent);
                record?.OnAgentError(payload.Turn, payload.Error);
            })));
        // The tool gate: every owned session's tool call asks the composed approval answerers,
        // which the approval/request listener below routes to the client as one requestPermission.
        _disposers.Add(ctx.On("tools/pre-execute",
            new Func<ToolRunContext, Func<Task<PreToolDecision>>, Task<PreToolDecision>>(OnPreExecuteAsync)));
        _disposers.Add(ctx.On(ApprovalService.RequestEvent,
            new Func<ApprovalRequest, Func<Task<ApprovalOutcome>>, Task<ApprovalOutcome>>(OnApprovalRequestAsync)));
    }

    /// <summary>
    /// Dispose the server-owned sessions and subscriptions to quiescence; the surrounding context
    /// keeps running.
    /// </summary>
    /// <returns>settlement of the quiescent teardown.</returns>
    public Task ShutdownAsync()
    {
        _shutdownTask ??= ShutdownCoreAsync();
        return _shutdownTask;
    }

    /// <summary>Dispatch one incoming request; an unknown method throws (→ the transport's <c>-32603</c>).</summary>
    public Task<JsonElement?> HandleRequestAsync(string method, JsonElement? parameters)
        => method switch
        {
            AcpProtocol.Initialize => Task.FromResult<JsonElement?>(Serialize(new
            {
                protocolVersion = AcpProtocol.ProtocolVersion,
                agentInfo = new { name = AcpProtocol.AgentName, version = AcpProtocol.AgentVersion },
                agentCapabilities = new
                {
                    // No MCP transports are mounted (the port's MCP reduction), so no capability
                    // is advertised.
                    mcpCapabilities = new { },
                    promptCapabilities = new { image = false, audio = false, embeddedContext = false },
                    sessionCapabilities = new { close = new { }, list = new { }, resume = new { } },
                },
                authMethods = Array.Empty<object>(),
            })),
            AcpProtocol.Authenticate => Task.FromResult<JsonElement?>(Serialize(new { })),
            AcpProtocol.SessionNew => NewSessionAsync(parameters
                ?? throw InvalidParams("session/new requires parameters")),
            AcpProtocol.SessionList => ListSessionsAsync(parameters
                ?? throw InvalidParams("session/list requires parameters")),
            AcpProtocol.SessionResume => ResumeSessionAsync(parameters
                ?? throw InvalidParams("session/resume requires parameters")),
            AcpProtocol.SessionClose => CloseSessionAsync(parameters
                ?? throw InvalidParams("session/close requires parameters")),
            AcpProtocol.SetSessionConfigOption => SetConfigOptionAsync(parameters
                ?? throw InvalidParams("session/setConfigOption requires parameters")),
            AcpProtocol.SessionPrompt => PromptAsync(parameters
                ?? throw InvalidParams("session/prompt requires parameters")),
            _ => throw new InvalidOperationException($"unknown ACP method: {method}"),
        };

    /// <summary>Dispatch one incoming notification (the <c>session/cancel</c> channel).</summary>
    public void HandleNotificationAsync(string method, JsonElement? parameters)
    {
        if (method != AcpProtocol.SessionCancel || parameters is not { } wire) return;
        var parsed = wire.Deserialize<CancelParams>(AcpWire.Json);
        if (parsed is not null && _sessions.TryGetValue(parsed.SessionId, out var record)) record.Cancel();
    }

    private async Task<JsonElement?> NewSessionAsync(JsonElement parameters)
    {
        AssertOpen();
        var wire = parameters.Deserialize<NewSessionParams>(AcpWire.Json)
            ?? throw InvalidParams("invalid session/new parameters");
        ValidateWorkspaceParams(wire.Cwd, wire.AdditionalDirectories);
        ValidateMcpServers(wire.McpServers);
        var sessionId = "session-" + Guid.NewGuid().ToString("N");
        var record = AcpSession.Create(_ctx, _loop, sessionId, SessionOptions(wire.Cwd),
            new AcpModelControl(_config.Provider, _config.Model), Notifier(sessionId));
        _sessions[sessionId] = record;
        try
        {
            var configOptions = record.ConfigOptions();
            AssertOpen();
            return Serialize(new { sessionId, configOptions });
        }
        catch
        {
            _sessions.Remove(sessionId);
            await record.CloseAsync("session/new activation failed");
            throw;
        }
    }

    private async Task<JsonElement?> ResumeSessionAsync(JsonElement parameters)
    {
        AssertOpen();
        var wire = parameters.Deserialize<ResumeSessionParams>(AcpWire.Json)
            ?? throw InvalidParams("invalid session/resume parameters");
        ValidateWorkspaceParams(wire.Cwd, null);
        ValidateMcpServers(wire.McpServers);
        if (_sessions.ContainsKey(wire.SessionId))
        {
            throw InvalidParams($"session is already active: {wire.SessionId}");
        }
        if (_persistence is null || !_persistence.Exists(new SessionId(wire.SessionId)))
        {
            throw InvalidParams($"session is not resumable: {wire.SessionId}");
        }
        // The port's persisted header carries no origin, parent, or cwd, so the resume checks are
        // existence only (documented reduction). The store's Remove releases the identity the
        // original creation left behind, exactly as the store documents for the resume flow.
        var sessions = _ctx.Get<SessionStore>("sessions")
            ?? throw new InvalidOperationException("acp requires the sessions row");
        sessions.Remove(new SessionId(wire.SessionId));
        var record = AcpSession.Resume(_ctx, _loop, wire.SessionId, SessionOptions(wire.Cwd),
            new AcpModelControl(_config.Provider, _config.Model), Notifier(wire.SessionId));
        _sessions[wire.SessionId] = record;
        try
        {
            return Serialize(new { configOptions = record.ConfigOptions() });
        }
        catch
        {
            _sessions.Remove(wire.SessionId);
            await record.CloseAsync("session/resume option discovery failed");
            throw;
        }
    }

    private Task<JsonElement?> ListSessionsAsync(JsonElement parameters)
    {
        AssertOpen();
        var wire = parameters.Deserialize<ListSessionsParams>(AcpWire.Json)
            ?? throw InvalidParams("invalid session/list parameters");
        if (wire.Cwd is not null && !Path.IsPathFullyQualified(wire.Cwd))
        {
            throw InvalidParams($"cwd must be an absolute path: {wire.Cwd}");
        }
        // The port's persisted headers carry no cwd, so the workspace filter is vacuous
        // (documented reduction); the cwd validation above still guards the parameter.
        var cursor = wire.Cursor is null ? null : DecodeCursor(wire.Cursor);
        var headers = _persistence is null
            ? Array.Empty<SessionHeader>()
            : _persistence.ListHeaders();
        var entries = headers
            .Where(header => !_sessions.ContainsKey(header.Id.Value))
            .Select(header => new SessionListEntry(header.Id.Value, header.CreatedAtMs))
            .OrderByDescending(entry => entry.CreatedAtMs)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .ToList();
        var remaining = cursor is null
            ? entries
            : entries.Where(entry => IsAfter(entry, cursor.Value)).ToList();
        var page = remaining.Take(_config.SessionListPageSize).ToList();
        SessionListEntry? next = remaining.Count > page.Count ? page[^1] : null;
        var result = new Dictionary<string, object?>
        {
            ["sessions"] = page.Select(entry => new { sessionId = entry.SessionId, cwd = "" }).ToArray(),
        };
        if (next is not null) result["nextCursor"] = EncodeCursor(next.CreatedAtMs, next.SessionId);
        return Task.FromResult<JsonElement?>(Serialize(result));
    }

    private async Task<JsonElement?> CloseSessionAsync(JsonElement parameters)
    {
        AssertOpen();
        var wire = parameters.Deserialize<CloseSessionParams>(AcpWire.Json)
            ?? throw InvalidParams("invalid session/close parameters");
        if (!_sessions.TryGetValue(wire.SessionId, out var record))
        {
            throw InvalidParams($"unknown session: {wire.SessionId}");
        }
        try
        {
            await record.CloseAsync("ACP session closed");
        }
        finally
        {
            if (_sessions.TryGetValue(wire.SessionId, out var current) && ReferenceEquals(current, record))
            {
                _sessions.Remove(wire.SessionId);
            }
        }
        return Serialize(new { });
    }

    private async Task<JsonElement?> SetConfigOptionAsync(JsonElement parameters)
    {
        AssertOpen();
        var wire = parameters.Deserialize<SetConfigOptionParams>(AcpWire.Json)
            ?? throw InvalidParams("invalid session/setConfigOption parameters");
        if (!_sessions.TryGetValue(wire.SessionId, out var record))
        {
            throw InvalidParams($"unknown session: {wire.SessionId}");
        }
        try
        {
            return Serialize(new { configOptions = record.SetConfig(wire.ConfigId, wire.Value) });
        }
        catch (AcpModelConfigError error)
        {
            throw InvalidParams(error.Message);
        }
    }

    private async Task<JsonElement?> PromptAsync(JsonElement parameters)
    {
        AssertOpen();
        var wire = parameters.Deserialize<PromptParams>(AcpWire.Json)
            ?? throw InvalidParams("invalid session/prompt parameters");
        if (!_sessions.TryGetValue(wire.SessionId, out var record))
        {
            throw InvalidParams($"unknown session: {wire.SessionId}");
        }
        return Serialize(await record.PromptAsync(wire.Prompt));
    }

    private async Task<PreToolDecision> OnPreExecuteAsync(ToolRunContext exec, Func<Task<PreToolDecision>> next)
    {
        var record = exec.Session is { } session ? BySession(session) : null;
        if (record is null) return await next();
        var approval = _ctx.Get<ApprovalService>("approval");
        if (approval is null) return await next();
        ApprovalOutcome outcome;
        try
        {
            outcome = await approval.AskAsync(new ApprovalRequest(record.Agent, exec.Name, exec.CallId.Value,
                Reason: null, CancellationToken: exec.CancellationToken));
        }
        catch (Exception error)
        {
            // An idle ask throws before appending; a broken answerer fails closed already. Either
            // way the call is denied, never run unapproved.
            _ctx.Logger.Warn($"acp: approval ask failed: {error.Message}");
            return new DenyDecision("the approval ask failed");
        }
        return outcome == ApprovalOutcome.AllowedOnce
            ? new AllowDecision()
            : new DenyDecision("denied by the ACP client");
    }

    /// <summary>The machine-policy channel: one-shot choices only, never a durable grant from an unknown client response.</summary>
    private async Task<ApprovalOutcome> OnApprovalRequestAsync(ApprovalRequest request, Func<Task<ApprovalOutcome>> next)
    {
        var record = OwnedRecord(request.Agent);
        if (record is null || request.CallId is null) return await next();
        var callId = request.CallId;
        await record.DrainUpdatesAsync();
        var result = await _transport.RequestAsync(AcpProtocol.ClientRequestPermission, Serialize(new
        {
            sessionId = record.Session.Id.Value,
            toolCall = new { toolCallId = callId },
            options = new object[]
            {
                new { optionId = "allow-once", name = "Allow once", kind = "allow_once" },
                new { optionId = "reject-once", name = "Reject", kind = "reject_once" },
            },
        }));
        var outcome = result is JsonElement element
            && element.TryGetProperty("outcome", out var outcomeValue)
            && outcomeValue.ValueKind == JsonValueKind.Object
                ? outcomeValue
                : throw new JsonRpcResponseError(-32603, "the ACP client answered requestPermission without an outcome");
        var kind = outcome.TryGetProperty("outcome", out var kindValue) && kindValue.ValueKind == JsonValueKind.String
            ? kindValue.GetString()
            : null;
        var optionId = outcome.TryGetProperty("optionId", out var optionValue) && optionValue.ValueKind == JsonValueKind.String
            ? optionValue.GetString()
            : null;
        if (kind == "cancelled") return ApprovalOutcome.Cancelled;
        return optionId == "allow-once" ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected;
    }

    private async Task ShutdownCoreAsync()
    {
        _closed = true;
        foreach (var record in _sessions.Values.ToArray())
        {
            try
            {
                await record.CloseAsync("ACP bridge disposed");
            }
            catch (Exception error)
            {
                _ctx.Logger.Warn($"acp: session teardown failed: {error.Message}");
            }
        }
        _sessions.Clear();
        foreach (var disposer in _disposers) disposer.Dispose();
        _disposers.Clear();
    }

    private AcpSession? OwnedRecord(Dsh.Agent.Agent agent)
    {
        _sessions.TryGetValue(agent.Session.Id.Value, out var record);
        return record is not null && record.Owns(agent) ? record : null;
    }

    private AcpSession? BySession(Dsh.Session.Session session)
    {
        _sessions.TryGetValue(session.Id.Value, out var record);
        return record is not null && record.OwnsSession(session) ? record : null;
    }

    private AgentOptions? SessionOptions(string? cwd = null)
        => _config.Provider is null && _config.Model is null && cwd is null
            ? null
            : new AgentOptions { Provider = _config.Provider, Model = _config.Model, Cwd = cwd };

    private Func<SessionUpdate, Task> Notifier(string sessionId)
        => update =>
        {
            // The object cast forces runtime-type serialization: the anonymous member's declared
            // type is the abstract SessionUpdate, which would serialize only the base members.
            _transport.Notify(AcpProtocol.ClientSessionUpdate,
                Serialize(new { sessionId, update = (object)update }));
            return Task.CompletedTask;
        };

    private void AssertOpen()
    {
        if (_closed) throw new JsonRpcResponseError(-32603, "the ACP bridge has been disposed");
    }

    private static JsonRpcResponseError InvalidParams(string detail) => new(-32602, detail);

    private static JsonElement? Serialize(object value) => JsonSerializer.SerializeToElement(value, AcpWire.Json);

    private static void ValidateWorkspaceParams(string cwd, JsonElement? additionalDirectories)
    {
        if (!Path.IsPathFullyQualified(cwd))
        {
            throw InvalidParams($"cwd must be an absolute path: {cwd}");
        }
        if (additionalDirectories is { } directories
            && directories.ValueKind == JsonValueKind.Array
            && directories.GetArrayLength() > 0)
        {
            throw InvalidParams("additionalDirectories is not supported");
        }
    }

    private static void ValidateMcpServers(JsonElement? mcpServers)
    {
        if (mcpServers is not { } element) return;
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw InvalidParams("mcpServers must be an array");
        }
        if (element.GetArrayLength() > 0)
        {
            throw InvalidParams("mcpServers mounts await the port's MCP client seam (not ported)");
        }
    }

    private sealed record SessionListEntry(string SessionId, long CreatedAtMs);

    /// <summary>Decode an opaque keyset cursor without assigning meaning to client metadata.</summary>
    private static (long CreatedAt, string SessionId)? DecodeCursor(string value)
    {
        if (value.Length == 0 || !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            throw InvalidParams("session/list cursor is invalid");
        }
        try
        {
            var unpadded = value.Replace('-', '+').Replace('_', '/');
            var padded = unpadded.PadRight(unpadded.Length + ((4 - unpadded.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parsed = JsonDocument.Parse(json).RootElement;
            if (parsed.ValueKind != JsonValueKind.Array || parsed.GetArrayLength() != 2)
            {
                throw new InvalidOperationException("invalid cursor fields");
            }
            var createdAt = parsed[0];
            var sessionId = parsed[1];
            if (createdAt.ValueKind != JsonValueKind.Number || !createdAt.TryGetInt64(out var created) || created < 0
                || sessionId.ValueKind != JsonValueKind.String || sessionId.GetString()!.Length == 0)
            {
                throw new InvalidOperationException("invalid cursor fields");
            }
            var canonical = EncodeCursor(created, sessionId.GetString()!);
            if (canonical != value) throw new InvalidOperationException("non-canonical cursor");
            return (created, sessionId.GetString()!);
        }
        catch (Exception error) when (error is not JsonRpcResponseError)
        {
            throw InvalidParams("session/list cursor is invalid");
        }
    }

    /// <summary>Encode the last returned ordering key as an opaque continuation token.</summary>
    private static string EncodeCursor(long createdAt, string sessionId)
    {
        var json = JsonSerializer.Serialize(new object[] { createdAt, sessionId });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Test whether an entry follows the cursor in newest-first list order.</summary>
    private static bool IsAfter(SessionListEntry entry, (long CreatedAt, string SessionId) cursor)
        => entry.CreatedAtMs < cursor.CreatedAt
            || (entry.CreatedAtMs == cursor.CreatedAt && string.CompareOrdinal(entry.SessionId, cursor.SessionId) > 0);
}

/// <summary>Parameters of the <c>session/cancel</c> notification.</summary>
public sealed record CancelParams(
    /// <summary>The ACP-owned session id.</summary>
    string SessionId);
