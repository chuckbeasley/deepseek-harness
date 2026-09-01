using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Agent;
using Harness.AgentLoop;
using Harness.Llm;
using Harness.Session;
using Harness.Spike;
using Harness.SystemPrompt;
using Harness.Tools;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The session remotes: the wire event projection, the history page, and the live follow stream
/// through the mux ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â all over real mock-LLM turns on the ported loop spine.
/// </summary>
public static class SessionRemotesTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Boot the loop spine with the mock adapter mounted.</summary>
    private static Context BootSpine(out SessionStore sessions, out global::Harness.AgentLoop.AgentLoop loop)
    {
        var ctx = new Context();
        sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new ToolRuntime(ctx);
        _ = new global::Harness.SystemPrompt.SystemPromptService(ctx);
        _ = new AgentRegistry(ctx);
        loop = new global::Harness.AgentLoop.AgentLoop(ctx);
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, new MockLlmProvider());
        return ctx;
    }

    /// <summary>Run one real mock-LLM turn on a session.</summary>
    private static async Task<global::Harness.Session.Session> RunTurnAsync(Context ctx, SessionStore sessions, global::Harness.AgentLoop.AgentLoop loop)
    {
        var id = new SessionId($"session-{Guid.NewGuid():N}");
        _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
        var driver = loop.GetLoop(id)
            ?? throw new InvalidOperationException("the loop published no driver");
        var message = new UserMessage
        {
            Id = new MessageId(Guid.NewGuid().ToString("N")),
            Content = new ContentBlock[] { new TextBlock("hello from the gateway test") },
            Source = new UserSource(),
        };
        driver.Send(message, InboxTarget.NextTurn, wakeup: true);
        await driver.WhenIdleAsync();
        return sessions.Get(id)!;
    }

    public static void WireProjection_LiftsEnvelopeOutOfData()
    {
        var evt = new UserMessageEvent
        {
            Seq = 7,
            Message = new UserMessage
            {
                Id = new MessageId("m-7"),
                Content = new ContentBlock[] { new TextBlock("hello") },
                Source = new UserSource(),
            },
            SurfaceOp = SurfaceOp.Append,
        };
        var wire = SessionWireEvent.Project(evt);
        Assert.True(wire.GetProperty("type").GetString() == "user/message", "the wire type");
        Assert.True(wire.GetProperty("seq").GetInt64() == 7, "the wire seq");
        Assert.True(wire.GetProperty("time").GetInt64() == evt.TimeMs, "the wire time");
        var data = wire.GetProperty("data");
        Assert.True(data.TryGetProperty("message", out _), "data carries the event-specific fields");
        Assert.False(data.TryGetProperty("id", out _), "the envelope id is not in data");
        Assert.False(data.TryGetProperty("seq", out _), "the envelope seq is not in data");
    }

    public static void Page_ReturnsWindowedRecords_OverRealTurns()
    {
        var ctx = BootSpine(out var sessions, out var loop);
        var session = RunTurnAsync(ctx, sessions, loop).GetAwaiter().GetResult();
        var throughSeq = session.Events.Last().Seq;
        var page = SessionRemotes.Page(ctx, sessions);
        var args = JsonSerializer.SerializeToElement(new
        {
            address = new { kind = "session", sessionId = session.Id.Value },
            throughSeq,
            maxMessages = 2,
        });
        var response = page.Invoke(args, CancellationToken.None).GetAwaiter().GetResult();
        var root = response!.Value;
        Assert.True(root.GetProperty("records").GetArrayLength() == 2, "the window bounds the records");
        Assert.True(root.GetProperty("hasMore").GetBoolean(), "earlier records remain");
        var first = root.GetProperty("records")[0].GetProperty("event");
        Assert.True(first.GetProperty("seq").GetInt64() == throughSeq - 1, "the window takes the latest records");
        ctx.Dispose();
    }

    public static void Page_UnknownSession_SettlesSessionNotFound()
    {
        var ctx = BootSpine(out var sessions, out var loop);
        var page = SessionRemotes.Page(ctx, sessions);
        var args = JsonSerializer.SerializeToElement(new
        {
            address = new { kind = "session", sessionId = "session-ghost" },
            throughSeq = 1,
        });
        var error = Assert.Throws<RpcSessionNotFoundError>(() =>
            page.Invoke(args, CancellationToken.None).GetAwaiter().GetResult());
        Assert.True(error.Message.Contains("session-ghost"), "the absent session is named");
        ctx.Dispose();
    }

    public static async Task Follow_SendsSnapshotThenLiveEvents_OverRealTurns()
    {
        var ctx = BootSpine(out var sessions, out var loop);
        var rpc = new DshRpcRegistry(ctx);
        using var follow = rpc.RegisterStream(SessionRemotes.Follow(ctx, sessions));
        var host = new WebHostService(ctx, new WebHostConfig(Port: FreePort(), AuthFence: false));
        host.StartAsync().GetAwaiter().GetResult();
        var id = new SessionId($"session-{Guid.NewGuid():N}");
        _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
        var session = sessions.Get(id)!;
        using var socket = new ClientWebSocket();
        var wsUrl = "ws://" + host.ListenUrl!["http://".Length..] + "/api/remote.mux";
        socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var open = JsonSerializer.SerializeToElement(new
            {
                type = "open",
                streamId = "follow-1",
                endpoint = "session/follow",
                payload = new { args = new { address = new { kind = "session", sessionId = id.Value } } },
            });
            socket.SendAsync(Encoding.UTF8.GetBytes(open.GetRawText()), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

            var snapshot = ReceiveItem(socket);
            Assert.True(snapshot.GetProperty("type").GetString() == "snapshot", "the snapshot item comes first");
            Assert.True(snapshot.GetProperty("cursor").GetInt64() == 0, "the cursor reflects the empty log");

            // A real turn now streams its events into the follow.
            var driver = loop.GetLoop(id)!;
            driver.Send(new UserMessage
            {
                Id = new MessageId(Guid.NewGuid().ToString("N")),
                Content = new ContentBlock[] { new TextBlock("live") },
                Source = new UserSource(),
            }, InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync();

            var probe1 = ReceiveItem(socket);
            Console.Error.WriteLine("DEBUG frame1: " + probe1.GetRawText());
            var probe2 = ReceiveItem(socket);
            Console.Error.WriteLine("DEBUG frame2: " + probe2.GetRawText());
            var firstLive = probe1;
            Assert.True(firstLive.TryGetProperty("type", out var liveKind) && liveKind.GetString() == "event", $"a live event frame arrives, got: {firstLive.GetRawText()}");
            var firstEvent = firstLive.GetProperty("event");
            Assert.True(firstEvent.TryGetProperty("seq", out var firstSeq) && firstSeq.GetInt64() == 0, $"the gap-free seq starts at the 0-based log head, got: {firstLive.GetRawText()}");
            // Drain the rest of the turn through its terminal event.
            var sawEnd = false;
            for (var i = 0; i < 20 && !sawEnd; i++)
            {
                var frame = ReceiveItem(socket);
                sawEnd = frame.TryGetProperty("type", out var frameType)
                    && frameType.GetString() == "event"
                    && frame.TryGetProperty("event", out var frameEvent)
                    && frameEvent.TryGetProperty("type", out var innerType)
                    && innerType.GetString() == "turn/end";
            }
            Assert.True(sawEnd, "the turn streams through its terminal event");

            // A synthetic skipped seq fails the stream with the gap error.
            ctx.Emit("session/event", session, new UserMessageEvent
            {
                Seq = 999,
                Message = new UserMessage
                {
                    Id = new MessageId("gap"),
                    Content = new ContentBlock[] { new TextBlock("gap") },
                    Source = new UserSource(),
                },
                SurfaceOp = SurfaceOp.Append,
            });
            var gap = ReceiveItem(socket);
            Assert.True(gap.GetProperty("type").GetString() == "error", "a skipped seq settles an error frame");
            Assert.True(gap.GetProperty("error").GetProperty("message").GetString().Contains("skipped seq", StringComparison.Ordinal), "the gap message");
        }
        finally
        {
            socket.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    /// <summary>Receive one mux item frame and return its value (stream items are wrapped).</summary>
    private static JsonElement ReceiveItem(ClientWebSocket socket)
    {
        var frame = Receive(socket);
        Assert.True(frame.GetProperty("type").GetString() == "item", "the frame is an item");
        return frame.GetProperty("value").Clone();
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
}



