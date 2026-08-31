using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cordis.Core;
using Dsh.Session;
using Dsh.Web.Host;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The WebSocket mux: the $events logical stream (ready first, then live emits), the error frame
/// for unknown endpoints, and the cancel path.
/// </summary>
public static class GatewayMuxTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (Context Ctx, WebHostService Host, ClientWebSocket Socket) Boot()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var rpc = new DshRpcRegistry(ctx);
        var host = new WebHostService(ctx, new WebHostConfig(Port: FreePort()));
        host.StartAsync().GetAwaiter().GetResult();
        var socket = new ClientWebSocket();
        var wsUrl = "ws://" + host.ListenUrl!["http://".Length..] + "/api/remote.mux";
        socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).GetAwaiter().GetResult();
        return (ctx, host, socket);
    }

    private static void Send(ClientWebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static JsonElement Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var memory = new MemoryStream();
        while (true)
        {
            var result = socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        using var document = JsonDocument.Parse(memory.ToArray());
        return document.RootElement.Clone();
    }

    public static void EventsStream_SendsReadyThenLiveEmits()
    {
        var (ctx, host, socket) = Boot();
        try
        {
            Send(socket, "{\"type\":\"open\",\"streamId\":\"s-1\",\"endpoint\":\"$events\",\"payload\":{\"args\":{}}}");
            var ready = Receive(socket);
            Assert.True(ready.GetProperty("type").GetString() == "ready", "the ready frame comes first");
            Assert.True(ready.GetProperty("clientId").GetString()!.Length > 0, "the client id is minted");
            Assert.True(ready.TryGetProperty("host", out var hostInfo) && hostInfo.TryGetProperty("home", out _), "the host info carries home");

            // A session create fires session/created on the shared context.
            var sessions = ctx.Get<SessionStore>("sessions");
            sessions!.Create();

            var emit = Receive(socket);
            Assert.True(emit.GetProperty("type").GetString() == "emit", "the emit frame arrives");
            Assert.True(emit.GetProperty("event").GetString() == "session/created", "the event name travels");
            Assert.True(emit.GetProperty("args").GetArrayLength() == 1, "the arg list carries the session");
        }
        finally
        {
            socket.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void UnknownEndpoint_AnswersErrorFrame()
    {
        var (ctx, host, socket) = Boot();
        try
        {
            Send(socket, "{\"type\":\"open\",\"streamId\":\"s-2\",\"endpoint\":\"no/such-stream\",\"payload\":{\"args\":{}}}");
            var frame = Receive(socket);
            Assert.True(frame.GetProperty("type").GetString() == "error", "the error frame arrives");
            Assert.True(frame.GetProperty("error").GetProperty("code").GetString() == "gateway/invocation-unavailable", "the coded failure");
        }
        finally
        {
            socket.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void Cancel_EndsTheLogicalStream()
    {
        var (ctx, host, socket) = Boot();
        try
        {
            Send(socket, "{\"type\":\"open\",\"streamId\":\"s-3\",\"endpoint\":\"$events\",\"payload\":{\"args\":{}}}");
            _ = Receive(socket); // ready
            Send(socket, "{\"type\":\"cancel\",\"streamId\":\"s-3\"}");
            // No terminal frame is delivered for an aborted stream; the socket stays usable.
            Send(socket, "{\"type\":\"open\",\"streamId\":\"s-4\",\"endpoint\":\"no/such-stream\",\"payload\":{\"args\":{}}}");
            var frame = Receive(socket);
            Assert.True(frame.GetProperty("type").GetString() == "error", "a second stream still works after the cancel");
        }
        finally
        {
            socket.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }
}
