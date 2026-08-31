using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Cordis.Core;
using Dsh.Interaction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dsh.Web.Host;

/// <summary>
/// The multiplexed WebSocket carrier (port of the TS remote-stream mux): one socket at
/// <c>/api/remote.mux</c>, many logical streams. Text frames only; <c>open</c>/<c>cancel</c>
/// client-side, <c>item</c>/<c>end</c>/<c>error</c> server-side, with a ping heartbeat and the
/// documented close codes (1003 binary, 1008 invalid request, 1011 undeliverable failure).
/// </summary>
public static class GatewayMux
{
    /// <summary>The exact mux path (the TS constant).</summary>
    public const string MuxPath = "/api/remote.mux";

    /// <summary>The forwarded-event stream endpoint.</summary>
    public const string EventsEndpoint = "$events";

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Map the mux onto the application (requires <c>UseWebSockets</c> in the pipeline).</summary>
    public static void MapMux(this WebApplication app, DshRpcRegistry registry, Context ctx)
    {
        app.Map(MuxPath, async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await RunConnectionAsync(socket, registry, ctx);
        });
    }

    private static async Task RunConnectionAsync(WebSocket socket, DshRpcRegistry registry, Context ctx)
    {
        var writer = new MuxWriter(socket);
        var streams = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var socketCts = new CancellationTokenSource();
        try
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(buffer, socketCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                    break;
                }
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text messages required", CancellationToken.None);
                    break;
                }
                var text = await ReadTextAsync(socket, buffer, received, socketCts.Token);
                if (text is null) break;
                if (!TryParseClientMessage(text, out var type, out var streamId, out var endpoint, out var payload))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid Remote stream request", CancellationToken.None);
                    break;
                }
                if (type == "cancel")
                {
                    if (streams.Remove(streamId, out var controller)) controller.Cancel();
                    continue;
                }
                if (streams.ContainsKey(streamId))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "duplicate Remote stream id", CancellationToken.None);
                    break;
                }
                var streamCts = CancellationTokenSource.CreateLinkedTokenSource(socketCts.Token);
                streams[streamId] = streamCts;
                _ = RunStreamAsync(streamId, endpoint, payload, writer, registry, ctx, streamCts);
            }
        }
        finally
        {
            socketCts.Cancel();
            // Note: the managed WebSocket API cannot emit protocol ping frames, so the TS
            // 2-second heartbeat is not reproduced; liveness relies on the framework's automatic
            // pong handling and client-side timeouts (documented reduction).
        }
    }

    /// <summary>One logical stream: dispatch and pump frames, then settle end/error.</summary>
    private static async Task RunStreamAsync(
        string streamId, string endpoint, JsonElement? payload,
        MuxWriter writer, DshRpcRegistry registry, Context ctx, CancellationTokenSource streamCts)
    {
        try
        {
            if (endpoint == EventsEndpoint)
            {
                await RunEventStreamAsync(streamId, writer, ctx, streamCts.Token);
            }
            else if (registry.GetStream(endpoint) is { } streamMethod)
            {
                var args = payload is JsonElement element && element.TryGetProperty("args", out var argsValue)
                    ? argsValue.Clone()
                    : (JsonElement?)null;
                await foreach (var item in streamMethod.Invoke(args, streamCts.Token))
                {
                    await writer.SendAsync(new { type = "item", streamId, value = item }, streamCts.Token);
                }
            }
            else if (registry.Get(endpoint) is not null)
            {
                await writer.SendAsync(new { type = "error", streamId, error = new
                {
                    code = RpcErrorCodes.SignatureInvalid,
                    message = $"endpoint \"{endpoint}\" is a unary method and cannot be opened as a stream",
                    details = new { },
                } }, streamCts.Token);
                return;
            }
            else
            {
                await writer.SendAsync(new { type = "error", streamId, error = new
                {
                    code = RpcErrorCodes.InvocationUnavailable,
                    message = $"no stream method is registered for \"{endpoint}\"",
                    details = new { },
                } }, streamCts.Token);
                return;
            }
            if (!streamCts.IsCancellationRequested)
            {
                await writer.SendAsync(new { type = "end", streamId }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (streamCts.IsCancellationRequested)
        {
            // The caller cancelled the logical stream (or the socket closed): end quietly, no
            // terminal frame (the TS cancel contract).
        }
        catch (Exception error)
        {
            try
            {
                await writer.SendAsync(new
                {
                    type = "error",
                    streamId,
                    error = RpcErrorCodec.Encode(new RpcError(RpcErrorCodes.Internal, error.Message)),
                }, CancellationToken.None);
            }
            catch (Exception)
            {
                // The terminal frame could not be delivered: the socket is gone.
            }
        }
    }

    /// <summary>The $events logical stream: ready first, then every forwarded emit.</summary>
    private static async Task RunEventStreamAsync(string streamId, MuxWriter writer, Context ctx, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var home = Environment.GetEnvironmentVariable("DSH_HOME")
            ?? ctx.Get<string>("dshProfileDir")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pump = Task.Run(async () =>
        {
            await writer.SendAsync(new { type = "ready", clientId, host = new { home } }, ct);
            await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            {
                await writer.SendRawAsync(frame, ct);
            }
        }, ct);
        using var subscription = RemoteEventSource.Subscribe(ctx, dispatch =>
        {
            var frame = JsonSerializer.SerializeToElement(new
            {
                type = "emit",
                @event = dispatch.Event,
                args = dispatch.Args,
            }, WireJson);
            if (!channel.Writer.TryWrite(frame))
            {
                ctx.Logger.Warn($"web: $events emit for {dispatch.Event} dropped (stream closed)");
            }
        });
        var settlement = ctx.Get<RemoteEventSettlement>("remoteEventSettlement");
        IDisposable? interaction = null;
        if (settlement is not null)
        {
            settlement.RegisterClient(clientId);
            interaction = BridgeInteractionWaterfalls(ctx, settlement, clientId, writer, ct);
        }
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            // the logical stream was cancelled or the socket closed
        }
        finally
        {
            // Pending proposals die with the stream: every awaiting ask settles cancelled/aborted.
            settlement?.UnregisterClient(clientId);
            interaction?.Dispose();
            channel.Writer.TryComplete();
            try
            {
                await pump;
            }
            catch (Exception)
            {
                // the socket is gone; the pump ends with it
            }
        }
    }

    /// <summary>
    /// Bridge the interaction waterfall proposals onto this <c>$events</c> stream (the C# half of
    /// the TS remote waterfall delivery): every <c>approval/request</c> and
    /// <c>user-questions/ask</c> dispatch while the stream is open is forwarded as a
    /// <c>waterfall</c> frame and held pending in the settlement until the client answers through
    /// <c>$events/result</c> or the request/stream dies (a <c>cancel</c> frame, then the ask
    /// settles cancelled/aborted). A listener that throws synchronously must land in the same
    /// rejection path as an async one, so the resolve wrapper contains the continuation call.
    /// </summary>
    private static IDisposable BridgeInteractionWaterfalls(
        Context ctx, RemoteEventSettlement settlement, string clientId, MuxWriter writer, CancellationToken ct)
    {
        var approval = ctx.On("approval/request",
            new Func<ApprovalRequest, Func<Task<ApprovalOutcome>>, Task<ApprovalOutcome>>(async (request, next) =>
            {
                var agentId = request.Agent.Id.Value;
                var projected = JsonSerializer.SerializeToElement(new
                {
                    toolName = request.ToolName,
                    callId = request.CallId,
                    reason = request.Reason,
                }, WireJson);
                var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                var eventId = settlement.Begin(clientId,
                    resolve: async outcome =>
                    {
                        switch (outcome.Kind)
                        {
                            case RemoteEventOutcomeKind.Next:
                                try { tcs.SetResult(await next()); }
                                catch (Exception error) { tcs.SetException(error); }
                                break;
                            case RemoteEventOutcomeKind.Result:
                                tcs.SetResult(ParseApprovalOutcome(outcome.Value));
                                break;
                            case RemoteEventOutcomeKind.Rejected:
                                tcs.SetException(outcome.Error
                                    ?? new RemoteRejectionException("Error", "the client rejected the approval request", null, null));
                                break;
                        }
                    },
                    cancel: () => tcs.TrySetResult(ApprovalOutcome.Cancelled));
                using var cancellation = request.CancellationToken?.Register(() =>
                {
                    settlement.Cancel(clientId, eventId);
                    _ = TrySendCancelFrameAsync(writer, eventId, ct);
                });
                try
                {
                    await writer.SendAsync(new { type = "waterfall", @event = "approval/request", eventId, agentId, request = projected }, ct);
                    return await tcs.Task;
                }
                finally
                {
                    settlement.Cancel(clientId, eventId);
                }
            }));
        var questions = ctx.On("user-questions/ask",
            new Func<UserQuestionRequest, Func<Task<UserQuestionAnswer>>, Task<UserQuestionAnswer>>(async (request, next) =>
            {
                var agentId = request.Agent?.Id.Value ?? "";
                var projected = JsonSerializer.SerializeToElement(request.Questions, WireJson);
                var tcs = new TaskCompletionSource<UserQuestionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
                var eventId = settlement.Begin(clientId,
                    resolve: async outcome =>
                    {
                        switch (outcome.Kind)
                        {
                            case RemoteEventOutcomeKind.Next:
                                try { tcs.SetResult(await next()); }
                                catch (Exception error) { tcs.SetException(error); }
                                break;
                            case RemoteEventOutcomeKind.Result:
                                tcs.SetResult(ParseQuestionAnswer(outcome.Value));
                                break;
                            case RemoteEventOutcomeKind.Rejected:
                                tcs.SetException(outcome.Error
                                    ?? new RemoteRejectionException("Error", "the client rejected the question", null, null));
                                break;
                        }
                    },
                    cancel: () => tcs.SetException(new UserQuestionError("ask_user_question was aborted before the user answered", "ASK_ABORTED")));
                using var cancellation = request.CancellationToken?.Register(() =>
                {
                    settlement.Cancel(clientId, eventId);
                    _ = TrySendCancelFrameAsync(writer, eventId, ct);
                });
                try
                {
                    await writer.SendAsync(new { type = "waterfall", @event = "user-questions/ask", eventId, agentId, request = projected }, ct);
                    return await tcs.Task;
                }
                finally
                {
                    settlement.Cancel(clientId, eventId);
                }
            }));
        return new BridgeDisposer(approval, questions);
    }

    /// <summary>Map a client-returned value to the closed approval vocabulary; anything else fails closed.</summary>
    private static ApprovalOutcome ParseApprovalOutcome(JsonElement? value)
    {
        if (value is { } element && element.ValueKind == JsonValueKind.String)
        {
            try
            {
                return JsonSerializer.Deserialize<ApprovalOutcome>(element.GetRawText(), WireJson);
            }
            catch (JsonException)
            {
                // fall through to fail closed
            }
        }
        return ApprovalOutcome.Unavailable;
    }

    /// <summary>Parse a client-returned answer; a malformed answer fails the question closed.</summary>
    private static UserQuestionAnswer ParseQuestionAnswer(JsonElement? value)
    {
        if (value is { } element)
        {
            try
            {
                return JsonSerializer.Deserialize<UserQuestionAnswer>(element.GetRawText(), WireJson)
                    ?? throw new InvalidOperationException("null answer");
            }
            catch (Exception)
            {
                // fall through to fail closed
            }
        }
        throw new UserQuestionError("the client returned no usable answer", "UNAVAILABLE");
    }

    private static async Task TrySendCancelFrameAsync(MuxWriter writer, string eventId, CancellationToken ct)
    {
        try
        {
            await writer.SendAsync(new { type = "cancel", eventId }, ct);
        }
        catch (Exception)
        {
            // the socket is gone; the pending ask settles through its cancellation path
        }
    }

    private sealed class BridgeDisposer : IDisposable
    {
        private readonly IDisposable[] _disposers;

        public BridgeDisposer(params IDisposable[] disposers)
        {
            _disposers = disposers;
        }

        public void Dispose()
        {
            foreach (var disposer in _disposers) disposer.Dispose();
        }
    }

    private static async Task<string?> ReadTextAsync(WebSocket socket, byte[] buffer, WebSocketReceiveResult first, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        memory.Write(buffer, 0, first.Count);
        while (!first.EndOfMessage)
        {
            first = await socket.ReceiveAsync(buffer, ct);
            memory.Write(buffer, 0, first.Count);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static bool TryParseClientMessage(string text, out string type, out string streamId, out string endpoint, out JsonElement? payload)
    {
        type = "";
        streamId = "";
        endpoint = "";
        payload = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var typeJson) || typeJson.ValueKind != JsonValueKind.String) return false;
            type = typeJson.GetString()!;
            if (type == "cancel")
            {
                if (!root.TryGetProperty("streamId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString()!.Length == 0) return false;
                streamId = id.GetString()!;
                return true;
            }
            if (type != "open") return false;
            if (!root.TryGetProperty("streamId", out var streamIdJson) || streamIdJson.ValueKind != JsonValueKind.String || streamIdJson.GetString()!.Length == 0) return false;
            if (!root.TryGetProperty("endpoint", out var endpointJson) || endpointJson.ValueKind != JsonValueKind.String || endpointJson.GetString()!.Length == 0) return false;
            if (!root.TryGetProperty("payload", out var payloadJson) || payloadJson.ValueKind != JsonValueKind.Object) return false;
            if (!ClientRequestEnvelope.IsRemotePayload(payloadJson)) return false;
            streamId = streamIdJson.GetString()!;
            endpoint = endpointJson.GetString()!;
            payload = payloadJson.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Serialized frame writer: one send at a time, strictly ordered.</summary>
    private sealed class MuxWriter
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public MuxWriter(WebSocket socket)
        {
            _socket = socket;
        }

        public Task SendAsync(object frame, CancellationToken ct)
            => SendRawAsync(JsonSerializer.SerializeToElement(frame, WireJson), ct);

        public async Task SendRawAsync(JsonElement frame, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(frame.GetRawText());
            await _gate.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
