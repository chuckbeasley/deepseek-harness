using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Agent;
using Harness.Interaction;
using Harness.Session;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The $events waterfall settlement over a real Kestrel host: the mux $events stream forwards an
/// approval/request proposal as a waterfall frame, the $events/result unary settles it, and the
/// abort path delivers the cancel frame. The wire shapes mirror the TS stream protocol.
/// </summary>
public static class RemoteEventSettlementTests
{
    public static void WaterfallFrame_AndResultSettlement_RoundTrip()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("ev1");
            using (socket)
            {
                var ask = harness.Approval.AskAsync(new ApprovalRequest(harness.Agent, "shell/run", Reason: "needs the sandbox"));
                var waterfall = harness.ReadUntilAsync(socket, "waterfall").GetAwaiter().GetResult();
                Assert.Equal("approval/request", waterfall.GetProperty("event").GetString(), "the forwarded event name");
                var eventId = waterfall.GetProperty("eventId").GetString()!;
                Assert.Equal(harness.Agent.Id.Value, waterfall.GetProperty("agentId").GetString(), "the agent identity rides along");
                Assert.Equal("shell/run", waterfall.GetProperty("request").GetProperty("toolName").GetString(), "the projected request");
                Assert.Equal("needs the sandbox", waterfall.GetProperty("request").GetProperty("reason").GetString(), "the projected request");

                var response = harness.PostResult(clientId, eventId, "result", "allowed-once");
                Assert.True(response.Ok, "the result is accepted");
                Assert.True(response.Result!.Value.GetProperty("settled").GetBoolean(), "the ack names the settlement");
                Assert.Equal(ApprovalOutcome.AllowedOnce, ask.GetAwaiter().GetResult(), "the ask resolves the granted outcome");
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void NextOutcome_DelegatesToTheContinuation()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("ev2");
            using (socket)
            {
                var ask = harness.Approval.AskAsync(new ApprovalRequest(harness.Agent, "shell/run"));
                var waterfall = harness.ReadUntilAsync(socket, "waterfall").GetAwaiter().GetResult();
                var response = harness.PostResult(clientId, waterfall.GetProperty("eventId").GetString()!, "next");
                Assert.True(response.Ok, "the delegation is accepted");
                Assert.Equal(ApprovalOutcome.Unavailable, ask.GetAwaiter().GetResult(),
                    "the continuation runs and fails closed with no further answerer");
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void RejectedOutcome_FailsTheAskClosed()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("ev3");
            using (socket)
            {
                var ask = harness.Approval.AskAsync(new ApprovalRequest(harness.Agent, "shell/run"));
                var waterfall = harness.ReadUntilAsync(socket, "waterfall").GetAwaiter().GetResult();
                var rejection = JsonSerializer.SerializeToElement(new { name = "Error", message = "the operator refused", code = "REFUSED" });
                var response = harness.PostResult(clientId, waterfall.GetProperty("eventId").GetString()!, "rejected", rejection: rejection);
                Assert.True(response.Ok, "the rejection is accepted");
                Assert.Equal(ApprovalOutcome.Unavailable, ask.GetAwaiter().GetResult(),
                    "a rejecting client listener fails the ask closed, never the caller");
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void AnAbortedRequest_DeliversTheCancelFrame_AndSettlesCancelled()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("ev4");
            using (socket)
            {
                using var cts = new CancellationTokenSource();
                var ask = harness.Approval.AskAsync(new ApprovalRequest(harness.Agent, "shell/run", CancellationToken: cts.Token));
                var waterfall = harness.ReadUntilAsync(socket, "waterfall").GetAwaiter().GetResult();
                var eventId = waterfall.GetProperty("eventId").GetString()!;
                cts.Cancel();
                var cancel = harness.ReadUntilAsync(socket, "cancel").GetAwaiter().GetResult();
                Assert.Equal(eventId, cancel.GetProperty("eventId").GetString(), "the cancel names the same proposal");
                Assert.Equal(ApprovalOutcome.Cancelled, ask.GetAwaiter().GetResult(), "the abort settles cancelled");
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void UnknownClientId_SettlesGatewayInternal()
    {
        using var harness = Harness.Start();
        try
        {
            var response = harness.PostResult("no-such-client", "whatever", "next");
            Assert.False(response.Ok, "an unknown client generation is refused");
            Assert.Equal("gateway/internal", response.Error!.Code);
            Assert.True(response.Error.Message.Contains("identifies no active event stream", StringComparison.Ordinal), "the failure names the stream rule");
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void UserQuestionsAsk_AnswersOverTheStream()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("evq");
            using (socket)
            {
                var ask = harness.UserQuestions.AskAsync(new UserQuestionRequest(new[] { new UserQuestionItem("q1", "Proceed?") }));
                var waterfall = harness.ReadUntilAsync(socket, "waterfall").GetAwaiter().GetResult();
                Assert.Equal("user-questions/ask", waterfall.GetProperty("event").GetString(), "the forwarded event name");
                Assert.Equal("q1", waterfall.GetProperty("request").EnumerateArray().First().GetProperty("id").GetString(), "the projected question");
                var answer = JsonSerializer.SerializeToElement(new { answers = new[] { new { id = "q1", selected = new[] { "Yes" } } } });
                var response = harness.PostResult(clientId, waterfall.GetProperty("eventId").GetString()!, "result", value: answer);
                Assert.True(response.Ok, "the answer is accepted");
                var result = ask.GetAwaiter().GetResult();
                Assert.Equal("q1", result.Answers.Single().Id, "the answer echoes the question id");
                Assert.Equal("Yes", result.Answers.Single().Selected.Single(), "the selected label rides back");
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void UnknownEventIdOnAKnownClient_AcksAsANoOp()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("ev5");
            using (socket)
            {
                var response = harness.PostResult(clientId, "missing-event", "next");
                Assert.True(response.Ok, "an unknown eventId on a known client is the TS no-op ack");
                Assert.True(response.Result!.Value.GetProperty("settled").GetBoolean(), "the ack still names the settlement");
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    public static void MalformedResultPayload_SettlesBadRequest()
    {
        using var harness = Harness.Start();
        try
        {
            var (socket, clientId) = harness.OpenEventsStream("ev6");
            using (socket)
            {
                var body = JsonSerializer.Serialize(new
                {
                    type = "client-request",
                    rpcId = "r-bad",
                    method = "$events/result",
                    payload = new { args = new { clientId, eventId = "x", outcome = new { kind = "bogus" } } },
                });
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/$events/result")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                var raw = harness.Client.SendAsync(request).GetAwaiter().GetResult();
                Assert.True((int)raw.StatusCode == 200, "the envelope round-trips");
                var root = JsonDocument.Parse(raw.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                Assert.Equal("server-response", root.GetProperty("type").GetString());
                Assert.False(root.GetProperty("result").GetProperty("ok").GetBoolean(), "the malformed outcome is refused");
                Assert.Equal("gateway/bad-request", root.GetProperty("result").GetProperty("error").GetProperty("code").GetString());
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    private sealed class Harness : IDisposable
    {
        public required Context Ctx { get; init; }
        public required WebHostService Host { get; init; }
        public required HttpClient Client { get; init; }
        public required ApprovalService Approval { get; init; }
        public required UserQuestionService UserQuestions { get; init; }
        public required global::Harness.Agent.Agent Agent { get; init; }

        public static Harness Start()
        {
            var ctx = new Context();
            var registry = new HshRpcRegistry(ctx);
            var settlement = new RemoteEventSettlement();
            ctx.Set("remoteEventSettlement", settlement);
            registry.Register(RemoteEventSettlement.ResultMethod(settlement));
            var approval = new ApprovalService(ctx);
            var userQuestions = new UserQuestionService(ctx);
            var store = new SessionStore(ctx);
            var session = store.Create(new SessionId("web-approval"));
            session.Append(new TurnStartEvent { Turn = 1 });
            var agent = new global::Harness.Agent.Agent(ctx, session);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var host = new WebHostService(ctx, new WebHostConfig(Port: port, AuthFence: false));
            host.StartAsync().GetAwaiter().GetResult();
            return new Harness
            {
                Ctx = ctx,
                Host = host,
                Client = new HttpClient { BaseAddress = new Uri(host.ListenUrl!) },
                Approval = approval,
                UserQuestions = userQuestions,
                Agent = agent,
            };
        }

        public (ClientWebSocket Socket, string ClientId) OpenEventsStream(string streamId)
        {
            var socket = new ClientWebSocket();
            socket.ConnectAsync(new Uri($"ws://{Host.ListenUrl!.Replace("http://", "")}/api/remote.mux"), CancellationToken.None).GetAwaiter().GetResult();
            var open = JsonSerializer.Serialize(new { type = "open", streamId, endpoint = "$events", payload = new { args = new { } } });
            socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(open)), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
            var ready = ReadUntilAsync(socket, "ready").GetAwaiter().GetResult();
            return (socket, ready.GetProperty("clientId").GetString()!);
        }

        public async Task<JsonElement> ReadUntilAsync(ClientWebSocket socket, string type)
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    stream.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);
                var frame = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
                if (frame.GetProperty("type").GetString() == type) return frame;
            }
        }

        public (bool Ok, JsonElement? Result, RpcError? Error) PostResult(string clientId, string eventId, string kind, object? value = null, JsonElement? rejection = null)
        {
            object outcome = kind switch
            {
                "next" => new { kind },
                "result" when value is not null => new { kind, value },
                "rejected" => new { kind, error = rejection },
                _ => new { kind },
            };
            var body = JsonSerializer.Serialize(new
            {
                type = "client-request",
                rpcId = "r-" + Guid.NewGuid().ToString("N"),
                method = "$events/result",
                payload = new { args = new { clientId, eventId, outcome } },
            });
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/$events/result")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            var raw = Client.SendAsync(request).GetAwaiter().GetResult();
            var root = JsonDocument.Parse(raw.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
            var result = root.GetProperty("result");
            if (result.GetProperty("ok").GetBoolean())
            {
                return (true, result.GetProperty("value").Clone(), null);
            }
            var error = result.GetProperty("error");
            return (false, null, new RpcError(
                error.GetProperty("code").GetString()!,
                error.GetProperty("message").GetString()!,
                error.TryGetProperty("details", out var details) ? details.Clone() : null));
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.StopAsync().GetAwaiter().GetResult();
            Ctx.Dispose();
        }
    }
}
