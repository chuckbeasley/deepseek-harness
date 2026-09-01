using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Lsp.Tests;

/// <summary>The JSON-RPC connection over a spawned fixture process (mirrors connection.spec.ts).</summary>
public static class ConnectionTests
{
    /// <summary>A server-request handler that answers nothing.</summary>
    private static readonly Func<string, JsonElement?, Task<JsonElement?>> NullHandler = (_, _) => Task.FromResult<JsonElement?>(null);

    /// <summary>Adapt a synchronous server-request handler to the connection's async seam.</summary>
    private static Func<string, JsonElement?, Task<JsonElement?>> Sync(Func<string, JsonElement?, JsonElement?> handler)
        => (method, parameters) => Task.FromResult(handler(method, parameters));

    private static LspConnectionSpec Spec(Dictionary<string, string> env, int maxStderrBytes = 100_000, int killGraceMs = 3_000)
        => new(LspTestHarness.FixtureCommand, LspTestHarness.FixtureArgs, Directory.GetCurrentDirectory(), env, 16_000_000, maxStderrBytes, killGraceMs, JsonSerializer.SerializeToElement(new { setting = 42 }));

    /// <summary>Terminate and await process close so no spawned fixture outlives the test.</summary>
    private static async Task CleanupAsync(LspConnection connection)
    {
        connection.Terminate();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static JsonElement Initialize(LspConnection connection)
    {
        var result = connection.Request("initialize", JsonSerializer.SerializeToElement(new { capabilities = new JsonObject() })).GetAwaiter().GetResult();
        Assert.True(result.HasValue, "initialize returns a result");
        return result.Value;
    }

    private static JsonElement DidOpenParams()
        => JsonSerializer.SerializeToElement(new { textDocument = new { uri = "file:///x", languageId = "ts", version = 1, text = "" } });

    public static async Task RequestResponse_RoundTripAndExposesPid()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env()), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var result = Initialize(connection);
            Assert.True(result.TryGetProperty("capabilities", out var caps), "the initialize result carries capabilities");
            Assert.True(caps.TryGetProperty("hoverProvider", out var hover) && hover.GetBoolean(), "hoverProvider is advertised");
            Assert.True(connection.Pid > 0, "the connection exposes a live pid");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task ForwardsExplicitEnvToChild()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ECHO_ENV", "DSH_LSP_TEST_FACT"), ("DSH_LSP_TEST_FACT", "managed"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            var hover = await connection.Request("textDocument/hover", null).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(hover.HasValue && hover.Value.TryGetProperty("contents", out var contents) && contents.GetString() == "managed", "the explicit env entry reaches the child");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task ScrubAmbientDshFacts_BeforeMergingExplicitEnv()
    {
        var old = Environment.GetEnvironmentVariable("DSH_LSP_SCRUB_ECHO");
        Environment.SetEnvironmentVariable("DSH_LSP_SCRUB_ECHO", "stale");
        try
        {
            var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ECHO_ENV", "DSH_LSP_SCRUB_ECHO"))), LspConnection.DefaultSpawner, NullHandler);
            try
            {
                Initialize(connection);
                var hover = await connection.Request("textDocument/hover", null).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(hover.HasValue && hover.Value.TryGetProperty("contents", out var contents) && contents.GetString() == "<DSH_LSP_SCRUB_ECHO unset>", "the ambient DSH_ fact is scrubbed from the child");
            }
            finally
            {
                await CleanupAsync(connection);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_LSP_SCRUB_ECHO", old);
        }
    }

    public static async Task ErrorResponse_RejectsRequest()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ERROR", "1"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("textDocument/hover", null));
            Assert.Contains("server refused the request", error.Message, "the error message surfaces");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task Terminate_AlreadyClosedChild_IsTeardownRace()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env()), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            connection.Terminate();
            await connection.Closed.WaitAsync(TimeSpan.FromSeconds(15));
            connection.Terminate(); // must not throw
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task AnswersWorkspaceConfigurationFromStaticConfig()
    {
        var seen = new List<(string Method, JsonElement? Parameters)>();
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "configuration"))), LspConnection.DefaultSpawner, Sync((method, parameters) =>
        {
            seen.Add((method, parameters));
            if (method == "workspace/configuration")
            {
                var items = parameters.HasValue && parameters.Value.TryGetProperty("items", out var itemsElement) ? itemsElement.GetArrayLength() : 0;
                var result = new JsonArray();
                for (var i = 0; i < items; i++) result.Add(JsonNode.Parse("{\"setting\":42}"));
                return JsonSerializer.SerializeToElement(result);
            }
            return null;
        }));
        try
        {
            Initialize(connection);
            await connection.Notify("textDocument/didOpen", DidOpenParams()).WaitAsync(TimeSpan.FromSeconds(30));
            await LspTestHarness.WaitForAsync(() => seen.Count > 0 && seen[0].Method == "workspace/configuration", "the handler sees workspace/configuration");
            Assert.Equal(1, seen.Count, "exactly one configuration request arrives");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task DropsServerNotificationWithoutReply()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "notification"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            await connection.Notify("textDocument/didOpen", DidOpenParams()).WaitAsync(TimeSpan.FromSeconds(30));
            // Give the notification a moment to be emitted and dropped.
            await Task.Delay(150);
            var hover = await connection.Request("textDocument/hover", null).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(hover.HasValue, "the connection stays usable after a dropped notification");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task ErrorResponse_WhenServerRequestHandlerRejects()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "applyEdit"))), LspConnection.DefaultSpawner, Sync((method, _) =>
        {
            if (method == "workspace/applyEdit") throw new InvalidOperationException("not permitted");
            return null;
        }));
        try
        {
            Initialize(connection);
            await connection.Notify("textDocument/didOpen", DidOpenParams()).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(150);
            var hover = await connection.Request("textDocument/hover", null).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(hover.HasValue, "the connection stays healthy after emitting the -32601 reply");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task GarbageBytesBeforeInitialize_AreTolerated()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_GARBAGE", "1"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var result = await connection.Request("initialize", JsonSerializer.SerializeToElement(new { capabilities = new JsonObject() })).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.HasValue && result.Value.TryGetProperty("capabilities", out _), "unframed bytes before a valid frame are absorbed into the header block");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task Request_AfterProcessCloses_Rejects()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env()), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            connection.Terminate();
            await connection.Closed.WaitAsync(TimeSpan.FromSeconds(15));
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("textDocument/hover", null));
            Assert.True(error.Message.Contains("exited") || error.Message.Contains("closed"), $"the close reason names the exit, got \"{error.Message}\"");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task Cancel_AfterClose_IsNoOp()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env()), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            connection.Terminate();
            await connection.Closed.WaitAsync(TimeSpan.FromSeconds(15));
            connection.Cancel(1); // must not throw
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task StderrTail_CappedAtMaxStderrBytes()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env()), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            Assert.True(connection.StderrTail.Length <= 100_000, "the retained stderr tail stays within the cap");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task SpawnFailure_RejectsRequest()
    {
        var spec = new LspConnectionSpec(
            "C:\\definitely\\not\\a\\real\\binary\\xyz",
            Array.Empty<string>(),
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            1_000,
            1_000,
            3_000,
            null);
        var connection = new LspConnection(spec, LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("initialize", null));
            Assert.Contains("failed to spawn", error.Message, "the spawn failure rejects the request");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task FramingError_KillsProcessAndFailsPending()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_BAD_LENGTH", "1"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("initialize", null));
            Assert.Contains("invalid Content-Length", error.Message, "the framing error rejects the pending request");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task IgnoresFramedNonObjectMessages()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_PREAMBLE", "[42, null]"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var result = await connection.Request("initialize", JsonSerializer.SerializeToElement(new { capabilities = new JsonObject() })).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.HasValue && result.Value.TryGetProperty("capabilities", out _), "framed non-objects are ignored and initialize resolves");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task DropsResponseForUnknownId()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_PREAMBLE", "[{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{\"stray\":true}}]"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var result = await connection.Request("initialize", JsonSerializer.SerializeToElement(new { capabilities = new JsonObject() })).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.HasValue && result.Value.TryGetProperty("capabilities", out _), "a stray response for an unknown id is dropped");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task StderrTail_CappedAcrossChunks()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_STDERR_FLOOD", "1")), maxStderrBytes: 100), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            await LspTestHarness.WaitForAsync(() => connection.StderrTail.Length >= 100, "the stderr tail reaches the cap");
            await Task.Delay(50);
            Assert.Equal(100, connection.StderrTail.Length, "the retained tail stays exactly at the cap");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task StderrTail_CappedByUtf8Bytes()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_EMOJI_STDERR", "1")), maxStderrBytes: 4), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            await connection.Closed.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("😀", connection.StderrTail, "the tail retains exactly one emoji (4 bytes)");
            Assert.Equal(4, Encoding.UTF8.GetByteCount(connection.StderrTail), "the retained emoji is 4 UTF-8 bytes");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task ErrorResponse_NoMessageString_FallsBack()
    {
        // LSP_FAKE_ERROR_NO_MESSAGE only strips the message from the LSP_FAKE_ERROR reply.
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_ERROR", "1"), ("LSP_FAKE_ERROR_NO_MESSAGE", "1"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            Initialize(connection);
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("textDocument/hover", null));
            Assert.Contains("LSP error response", error.Message, "an error without a message falls back");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task PendingRequest_RejectsWhenProcessExitsMidFlight()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_HANG_INITIALIZE", "1"), ("LSP_FAKE_EXIT_AFTER_MS", "100"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("initialize", null));
            Assert.True(error.Message.Contains("exited") || error.Message.Contains("closed"), $"the exit message rejects the pending request, got \"{error.Message}\"");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task StdinWriteFailure_RejectsPending_ProcessStaysAlive()
    {
        var failure = new InvalidOperationException("fixture stdin failure");
        LspConnectionWriter writer = (_, _, done) => done(failure);
        var connection = new LspConnection(Spec(LspTestHarness.Env()), LspConnection.DefaultSpawner, NullHandler, writer);
        try
        {
            var error = await Assert.ThrowsAsync<Exception>(() => connection.Request("initialize", null));
            Assert.Contains("fixture stdin failure", error.Message, "the injected write failure rejects the request");
            Assert.True(LspTestHarness.ProcessAlive(connection.Pid), "the process stays alive after the writer failure");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    public static async Task IgnoresFrameNeitherRequestNorNumericIdResponse()
    {
        var connection = new LspConnection(Spec(LspTestHarness.Env(("LSP_FAKE_PREAMBLE", "[{\"jsonrpc\":\"2.0\",\"id\":\"str-id\"}]"))), LspConnection.DefaultSpawner, NullHandler);
        try
        {
            var result = await connection.Request("initialize", JsonSerializer.SerializeToElement(new { capabilities = new JsonObject() })).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.HasValue && result.Value.TryGetProperty("capabilities", out _), "a string-id frame is ignored and initialize resolves");
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }
}
