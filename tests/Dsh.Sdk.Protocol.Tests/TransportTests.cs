using System.IO.Pipelines;
using System.Text.Json;
using Dsh.Sdk.Protocol;

namespace Dsh.Sdk.Protocol.Tests;

/// <summary>
/// The newline-delimited JSON-RPC transport: request/response correlation, error frames, handler
/// and notification wiring, malformed-line tolerance, cancellation, and closure semantics — over
/// two crossed in-memory pipe pairs (the .NET equivalent of the TS in-memory stream fixture).
/// </summary>
public static class TransportTests
{
    public static void RequestResponse_RoundTripsParamsAndResult()
    {
        using var pair = PeerPair.Create();
        try
        {
            var received = new List<string>();
            pair.Server.OnRequest((method, parameters) =>
            {
                received.Add(method);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { echo = parameters!.Value.GetProperty("x").GetInt32() }));
            });
            pair.Server.Start();
            pair.Client.Start();
            var result = pair.Client.RequestAsync("echo", new { x = 42 }).GetAwaiter().GetResult();
            Assert.Equal(42, result!.Value.GetProperty("echo").GetInt32(), "the result round-trips");
            Assert.Equal("echo", received.Single(), "the handler saw the method");
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void HandlerFailure_AnswersInternalError()
    {
        using var pair = PeerPair.Create();
        try
        {
            pair.Server.OnRequest((_, _) => throw new InvalidOperationException("handler bug"));
            pair.Server.Start();
            pair.Client.Start();
            var error = Assert.ThrowsAny<JsonRpcResponseError>(
                () => pair.Client.RequestAsync("boom", new { }),
                "a handler failure must surface as an error response");
            Assert.Equal(-32603, error.Code, "the internal-error code");
            Assert.Equal("handler bug", error.Message, "the message rides along");
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void MissingHandler_AnswersMethodNotFound()
    {
        using var pair = PeerPair.Create();
        try
        {
            pair.Server.Start();
            pair.Client.Start();
            var error = Assert.ThrowsAny<JsonRpcResponseError>(
                () => pair.Client.RequestAsync("nope", new { }),
                "a missing handler must answer -32601");
            Assert.Equal(-32601, error.Code, "the method-not-found code");
            Assert.True(error.Message.Contains("method not found", StringComparison.Ordinal), "the failure names the rule");
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void Notifications_AreDelivered_WithAndWithoutParams()
    {
        using var pair = PeerPair.Create();
        try
        {
            var notifications = new List<(string Method, JsonElement? Parameters)>();
            pair.Server.OnNotification((method, parameters) => notifications.Add((method, parameters)));
            pair.Server.Start();
            pair.Client.Start();
            pair.Client.Notify("ping", new { value = 7 });
            pair.Client.Notify("pong");
            WaitUntil(() => notifications.Count == 2);
            Assert.Equal("ping", notifications[0].Method, "the method rides along");
            Assert.Equal(7, notifications[0].Parameters!.Value.GetProperty("value").GetInt32(), "the params ride along");
            Assert.Equal("pong", notifications[1].Method, "the second notification arrives");
            Assert.Null(notifications[1].Parameters, "omitted params produce no params member");
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void NotificationWithoutHandler_IsDropped()
    {
        using var pair = PeerPair.Create();
        try
        {
            pair.Server.OnRequest((method, parameters) =>
                Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { ok = true })));
            pair.Server.Start();
            pair.Client.Start();
            pair.Client.Notify("orphan", new { });
            // No handler: the frame must be dropped, not answered or thrown — the next request
            // still round-trips, proving the orphan did not break the line framing.
            var result = pair.Client.RequestAsync("after", new { }).GetAwaiter().GetResult();
            Assert.True(result!.Value.GetProperty("ok").GetBoolean(), "the transport keeps working after the orphan");
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void MalformedLines_AreIgnored()
    {
        using var pair = PeerPair.Create();
        try
        {
            pair.Server.OnRequest((method, parameters) =>
                Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { ok = true })));
            pair.Server.Start();
            pair.Client.Start();
            pair.WriteRaw("not json\n");
            pair.WriteRaw("{broken\n");
            pair.WriteRaw("[1,2]\n");
            var result = pair.Client.RequestAsync("fine", new { }).GetAwaiter().GetResult();
            Assert.True(result!.Value.GetProperty("ok").GetBoolean(), "a request after garbage still round-trips");
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void Cancellation_RemovesThePendingRequest()
    {
        using var pair = PeerPair.Create();
        try
        {
            var gate = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Server.OnRequest((_, _) => gate.Task);
            pair.Server.Start();
            pair.Client.Start();
            using var cts = new CancellationTokenSource();
            var request = pair.Client.RequestAsync("slow", new { }, cts.Token);
            cts.Cancel();
            Console.WriteLine("TEST-DIAG: after cancel");
            var error = Assert.ThrowsAny<OperationCanceledException>(
                () => { request.GetAwaiter().GetResult(); return Task.CompletedTask; },
                "the aborted request rejects with cancellation");
            _ = error;
            gate.TrySetResult(JsonSerializer.SerializeToElement(new { late = true }));
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void Close_RejectsPendingRequests()
    {
        using var pair = PeerPair.Create();
        try
        {
            var gate = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Server.OnRequest((_, _) => gate.Task);
            pair.Server.Start();
            pair.Client.Start();
            var request = pair.Client.RequestAsync("slow", new { });
            pair.Client.Close();
            var error = Assert.ThrowsAny<IOException>(
                () => { request.GetAwaiter().GetResult(); return Task.CompletedTask; },
                "closing the transport rejects the pending request");
            Assert.Equal("JSON-RPC transport closed", error.Message, "the closure names the rule");
            gate.TrySetResult(JsonSerializer.SerializeToElement(new { late = true }));
        }
        finally
        {
            pair.Dispose();
        }
    }

    public static void InputEnd_FailsPendingRequests()
    {
        using var pair = PeerPair.Create();
        try
        {
            var gate = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Server.OnRequest((_, _) => gate.Task);
            pair.Server.Start();
            pair.Client.Start();
            var request = pair.Client.RequestAsync("slow", new { });
            pair.EndInput();
            var error = Assert.ThrowsAny<IOException>(
                () => { request.GetAwaiter().GetResult(); return Task.CompletedTask; },
                "the peer closing its output fails the pending request");
            Assert.Equal("JSON-RPC input closed", error.Message, "the failure names the rule");
            gate.TrySetResult(JsonSerializer.SerializeToElement(new { late = true }));
        }
        finally
        {
            pair.Dispose();
        }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new AssertionException("condition not met within timeout");
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Two transports crossed over in-memory pipes: the server's input is the client's output and
    /// vice versa. The client exposes raw-write and input-end hooks for the wire-tolerance tests.
    /// </summary>
    private sealed class PeerPair : IDisposable
    {
        private readonly Pipe _serverToClient = new();
        private readonly Pipe _clientToServer = new();
        private readonly JsonRpcLineTransport _server;
        private readonly JsonRpcLineTransport _client;
        private readonly StreamWriter _clientRaw;

        private PeerPair()
        {
            _server = new JsonRpcLineTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream());
            _client = new JsonRpcLineTransport(_serverToClient.Reader.AsStream(), _clientToServer.Writer.AsStream());
            _clientRaw = new StreamWriter(_clientToServer.Writer.AsStream(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        }

        public JsonRpcLineTransport Server => _server;

        public JsonRpcLineTransport Client => _client;

        public void WriteRaw(string line)
        {
            _clientRaw.Write(line);
            _clientRaw.Flush();
        }

        public void EndInput()
        {
            // The peer "exited": its output to us ends (our reader sees EOF) and our output to it
            // is no longer read (its reader sees EOF too), so both sides' pendings fail closed.
            _clientToServer.Writer.Complete();
            _serverToClient.Writer.Complete();
        }

        public static PeerPair Create() => new();

        public void Dispose()
        {
            _client.Close();
            _server.Close();
            _serverToClient.Writer.Complete();
            _clientToServer.Writer.Complete();
            _clientRaw.Dispose();
        }
    }
}





