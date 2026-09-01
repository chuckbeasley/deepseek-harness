using System.Text.Json;
using Harness.Sdk.Protocol;
using Harness.Spike;

namespace Harness.Sdk.Client.Tests;

/// <summary>
/// The SDK client against the REAL runtime: the built dsh CLI booting the <c>sdk</c> profile
/// (base spine + the stdio JSON-RPC server) as a child process. Proves the full
/// initialize → prompt → turn → shutdown round trip over process stdio, the subscription and
/// session-tree scoping, the timeout abandonment, and the close ladder.
/// </summary>
public static class ClientE2eTests
{
    public static void Initialize_ReturnsTheWireIdentity_AndUnknownMethodsAnswerTheProtocolError()
    {
        using var temp = TempDir.Create();
        using var client = new HarnessClient(null, Runtime.Resolve(temp.Path, temp.Path));
        try
        {
            client.Start();
            var identity = client.InitializeAsync(new InitializeParams(temp.Path, MockLlmProvider.Provider, MockLlmProvider.Model))
                .GetAwaiter().GetResult();
            Assert.Equal(SdkProtocol.ServerName, identity.Info.Name, "the wire-stable server name");
            Assert.Equal("0.0.1", identity.Info.Version, "the server version");
            var error = Assert.ThrowsAny<JsonRpcResponseError>(
                () => client.RequestAsync("no/such/method"),
                "an unknown method answers a protocol error");
            Assert.Equal(-32603, error.Code, "the runtime answers -32603 for an unknown method");
        }
        finally
        {
            client.CloseAsync().GetAwaiter().GetResult();
        }
    }

    public static void ATurn_StreamsNotifications_SettlesIdle_AndScopesToTheSessionTree()
    {
        using var temp = TempDir.Create();
        using var client = new HarnessClient(null, Runtime.Resolve(temp.Path, temp.Path));
        try
        {
            client.Start();
            client.InitializeAsync(new InitializeParams(temp.Path, MockLlmProvider.Provider, MockLlmProvider.Model))
                .GetAwaiter().GetResult();
            var root = "session-e2e-tree";
            using var rootSub = client.SubscribeSessionTree(root);
            using var otherSub = client.SubscribeSessionTree("session-unrelated");
            using var statusSub = client.Subscribe(notification =>
                notification.Method == SdkProtocol.SessionStatus && SdkWire.SessionMatches(notification, root));
            var messageId = client.PromptAsync(root, SdkWire.NormalizeInput("plan the round")).GetAwaiter().GetResult();
            Assert.True(messageId.Length > 0, "the prompt returns a message id");

            var idleSeen = false;
            var deadline = Environment.TickCount64 + 30_000;
            while (!idleSeen && Environment.TickCount64 < deadline)
            {
                var status = statusSub.NextAsync().GetAwaiter().GetResult();
                idleSeen = SdkWire.IsIdle(status);
            }
            Assert.True(idleSeen, "the session settles idle");

            var sawAssistant = false;
            var sawStatus = false;
            var sawReceipt = false;
            while (rootSub.TryNext() is { } notification)
            {
                if (notification.Method == SdkProtocol.SessionStatus) sawStatus = true;
                if (notification.Method != SdkProtocol.SessionEvent) continue;
                var evt = notification.Params.GetProperty("event");
                if (evt.GetProperty("type").GetString() == "assistant/message") sawAssistant = true;
                if (SdkWire.IsInboxReceipt(notification.Params, messageId)) sawReceipt = true;
            }
            Assert.True(sawReceipt, "the tree subscription received the enqueue receipt");
            Assert.True(sawAssistant, "the tree subscription received the assistant message");
            Assert.True(sawStatus, "the tree subscription received the status transitions");
            Assert.Null(otherSub.TryNext(), "an unrelated session tree receives nothing");
        }
        finally
        {
            client.CloseAsync().GetAwaiter().GetResult();
        }
    }

    public static void ARequestTimeout_AbandonsTheCall_AndClose_QuiescesTheRuntime()
    {
        using var client = new HarnessClient(null, Runtime.SilentChild());
        client.Start();
        var timeout = Assert.ThrowsAny<RequestTimeoutError>(
            () => client.RequestAsync("initialize", new { }, 50),
            "a bounded request against a silent runtime times out");
        Assert.Contains("initialize timed out after 50ms", timeout.Message, "the timeout names the method and bound");
        // The ladder: a refused shutdown, the EOF quiesce window, then the forced kill.
        client.CloseAsync().GetAwaiter().GetResult();
        var closed = Assert.ThrowsAny<TransportClosedError>(
            () => client.RequestAsync("initialize", new { }),
            "a request after close fails with the closed error");
        Assert.Contains("client is closed", closed.Message, "the closed error names the terminal state");
        using var late = client.Subscribe();
        var failed = Assert.ThrowsAny<TransportClosedError>(() => late.NextAsync(), "a subscription after close is born failed");
        Assert.Contains("exit code:", failed.Message, "the failure carries the process exit code");
    }

    public static void DeepSeekHarness_RunsATurn_AndReturnsTheOwnedInterval()
    {
        using var temp = TempDir.Create();
        var harness = new DeepSeekHarness(new DeepSeekHarnessOptions
        {
            DshBin = Runtime.CliPath,
            DshHome = temp.Path,
            ProcessCwd = temp.Path,
            Cwd = temp.Path,
            Provider = MockLlmProvider.Provider,
            Model = MockLlmProvider.Model,
        });
        try
        {
            var observed = new List<HarnessNotification>();
            var result = harness.RunAsync("plan the client port", new RunOptions { OnNotification = observed.Add })
                .GetAwaiter().GetResult();
            Assert.True(result.SessionId.StartsWith("session-", StringComparison.Ordinal), "the run mints a session id");
            Assert.Equal("Todo list recorded.", result.FinalResponse, "the mock turn's final text");
            Assert.True(result.Events.Count > 0, "the interval collected events");
            Assert.True(result.Notifications.Count > 0, "the interval collected notifications");
            Assert.True(result.Events.Any(e => e.Type == "user/message"), "the receipt event opens the interval");
            Assert.True(result.Events.Any(e => e.Type == "turn/end"), "the turn end closes the interval");
            Assert.True(result.Events.Any(e => e.Type == "todo/write"), "a plugin event unknown to the client passes through");
            Assert.True(result.Notifications.Any(n => n.Method == SdkProtocol.SessionStatus && SdkWire.IsIdle(n)),
                "the idle transition is in the interval");
            Assert.True(observed.Count == result.Notifications.Count, "the observer saw every notification");
            var second = harness.RunAsync("again", new RunOptions { SessionId = result.SessionId }).GetAwaiter().GetResult();
            Assert.Equal(result.SessionId, second.SessionId, "the named session is reused");
            Assert.Equal("Todo list recorded.", second.FinalResponse, "the second mock turn answers the same text");
        }
        finally
        {
            harness.Dispose();
        }
    }
}
