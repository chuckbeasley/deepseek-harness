using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp;

namespace FakeLspServer;

/// <summary>
/// A scriptable fake LSP server over stdio (port of <c>fixture-server.ts</c>). It speaks the real
/// Content-Length-framed base protocol so it exercises the client's framing, initialize handshake,
/// transient open/close, request mapping, and teardown — without a real language server. Behavior is
/// driven by the LSP_FAKE_* environment contract (identical to the TS fixture); a small set of
/// C#-only test hooks (LSP_FAKE_BAD_LENGTH, LSP_FAKE_PREAMBLE, LSP_FAKE_ERROR_NO_MESSAGE,
/// LSP_FAKE_HANG_INITIALIZE, LSP_FAKE_EXIT_AFTER_MS, LSP_FAKE_STDERR_FLOOD, LSP_FAKE_EMOJI_STDERR,
/// LSP_FAKE_HONOR_CANCEL, LSP_FAKE_TRAP_SIGTERM, LSP_FAKE_SPAWN_HELPER, LSP_FAKE_HELPER) back the
/// inline-script scenarios the TS suite runs through node -e.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object StdoutGate = new();
    private static readonly Dictionary<long, string> PendingServerRequests = new();
    private static long _serverRequestId = 10_000;
    private static volatile bool _paused;

    public static int Main()
    {
        var env = BuildEnv();
        if (env.EmojiStderr)
        {
            var bytes = Encoding.UTF8.GetBytes("😀😀");
            var stderr = Console.OpenStandardError();
            stderr.Write(bytes, 0, bytes.Length);
            stderr.Flush();
            return 0;
        }
        if (IsOne("LSP_FAKE_HELPER"))
        {
            // Helper mode: a sleeping child that only the tree kill can stop.
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (env.BadLength) WriteRaw("Content-Length: abc\r\n\r\n{}");
        if (env.SpawnHelperMarker is not null) SpawnHelper(env.SpawnHelperMarker);
        if (env.StderrFlood) _ = Task.Run(FloodStderr);
        if (env.ExitAfterMs > 0) _ = Task.Delay(env.ExitAfterMs).ContinueWith(_ => Environment.Exit(0));
        RegisterSignalHandlers(env);
        RunReadLoop(env);
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static FixtureEnv BuildEnv()
    {
        return new FixtureEnv(
            Enc: GetEnv("LSP_FAKE_ENCODING") ?? "utf-16",
            Sync: JsonEnv("LSP_FAKE_SYNC") ?? JsonNode.Parse("1"),
            ExtraCaps: JsonEnv("LSP_FAKE_CAPS"),
            Def: JsonEnv("LSP_FAKE_DEF"),
            Refs: JsonEnv("LSP_FAKE_REFS"),
            Impl: JsonEnv("LSP_FAKE_IMPL"),
            Hover: JsonEnv("LSP_FAKE_HOVER"),
            EchoEnv: GetEnv("LSP_FAKE_ECHO_ENV"),
            Hang: IsOne("LSP_FAKE_HANG"),
            CrashOnOpen: IsOne("LSP_FAKE_CRASH_ON_OPEN"),
            ExitAfterReply: IsOne("LSP_FAKE_EXIT_AFTER_REPLY"),
            ReplyDelayMs: IntEnv("LSP_FAKE_REPLY_DELAY_MS", 0),
            OpenMarker: GetEnv("LSP_FAKE_OPEN_MARKER"),
            InitializedMarker: GetEnv("LSP_FAKE_INITIALIZED_MARKER"),
            PauseStdin: IsOne("LSP_FAKE_PAUSE_STDIN_AFTER_INITIALIZED"),
            ExitDelayMs: IntEnv("LSP_FAKE_EXIT_DELAY_MS", 0),
            ExitMarker: GetEnv("LSP_FAKE_EXIT_MARKER"),
            NoShutdown: IsOne("LSP_FAKE_NO_SHUTDOWN"),
            OnOpen: GetEnv("LSP_FAKE_ON_OPEN"),
            ErrorReply: IsOne("LSP_FAKE_ERROR"),
            Garbage: IsOne("LSP_FAKE_GARBAGE"),
            BadLength: IsOne("LSP_FAKE_BAD_LENGTH"),
            Preamble: JsonEnv("LSP_FAKE_PREAMBLE"),
            ErrorNoMessage: IsOne("LSP_FAKE_ERROR_NO_MESSAGE"),
            HangInitialize: IsOne("LSP_FAKE_HANG_INITIALIZE"),
            ExitAfterMs: IntEnv("LSP_FAKE_EXIT_AFTER_MS", 0),
            StderrFlood: IsOne("LSP_FAKE_STDERR_FLOOD"),
            EmojiStderr: IsOne("LSP_FAKE_EMOJI_STDERR"),
            HonorCancel: IsOne("LSP_FAKE_HONOR_CANCEL"),
            TrapSigterm: IsOne("LSP_FAKE_TRAP_SIGTERM"),
            SpawnHelperMarker: GetEnv("LSP_FAKE_SPAWN_HELPER"));
    }

    private static void RunReadLoop(FixtureEnv env)
    {
        var decoder = new MessageDecoder(64 * 1024 * 1024);
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[16384];
        while (true)
        {
            int read;
            try
            {
                read = stdin.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                Environment.Exit(0);
                return;
            }
            if (read == 0)
            {
                // The client closed stdin; the process has nothing left to serve.
                Environment.Exit(0);
                return;
            }
            JsonRpcMessage[] messages;
            try
            {
                messages = decoder.Push(buffer.AsMemory(0, read));
            }
            catch
            {
                Environment.Exit(1);
                return;
            }
            foreach (var message in messages)
            {
                Handle(message, env);
                if (env.PauseStdin && _paused) return;
            }
        }
    }

    private static void Handle(JsonRpcMessage message, FixtureEnv env)
    {
        var id = message.Id;
        var method = message.Method;
        // A frame with an id but no method is the client's REPLY to a server→client request; log it.
        if (method is null && id is { } replyId && PendingServerRequests.Remove(replyId, out var kind))
        {
            Console.Error.WriteLine($"REPLY {kind} {JsonSerializer.Serialize(new { result = Raw(message.Result), error = Raw(message.Error) }, Options)}");
            return;
        }
        if (method == "initialize")
        {
            if (env.HangInitialize) return;
            if (env.Garbage) WriteRaw("this is not a framed message\r\n");
            if (env.Preamble is JsonArray preamble)
            {
                foreach (var item in preamble) WriteFrame(item);
            }
            var capabilities = new JsonObject
            {
                ["positionEncoding"] = env.Enc,
                ["textDocumentSync"] = env.Sync?.DeepClone(),
                ["definitionProvider"] = true,
                ["referencesProvider"] = true,
                ["implementationProvider"] = true,
                ["hoverProvider"] = true,
            };
            if (env.ExtraCaps is JsonObject extra)
            {
                foreach (var (key, value) in extra) capabilities[key] = value?.DeepClone();
            }
            Send(new JsonRpcMessage(Id: id, Result: JsonSerializer.SerializeToElement(new JsonObject { ["capabilities"] = capabilities }, Options)));
            return;
        }
        if (method == "shutdown")
        {
            if (env.NoShutdown) return;
            Send(new JsonRpcMessage(Id: id, Result: NullElement()));
            return;
        }
        if (method == "exit")
        {
            MarkExit(env, "EXIT");
            if (env.ExitDelayMs > 0)
            {
                _ = Task.Delay(env.ExitDelayMs).ContinueWith(_ =>
                {
                    MarkExit(env, "CLEAN");
                    Environment.Exit(0);
                });
                return;
            }
            MarkExit(env, "CLEAN");
            Environment.Exit(0);
            return;
        }
        if (method == "textDocument/didOpen")
        {
            if (env.CrashOnOpen) Environment.Exit(1);
            if (env.OpenMarker is not null)
            {
                string? text = null;
                if (message.Params is { } parameters
                    && parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("textDocument", out var textDocument)
                    && textDocument.ValueKind == JsonValueKind.Object
                    && textDocument.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    text = textElement.GetString();
                }
                File.AppendAllText(env.OpenMarker, JsonSerializer.Serialize(text, Options) + "\n");
            }
            if (env.OnOpen is not null) EmitServerRequest(env.OnOpen);
            return;
        }
        if (method == "initialized")
        {
            if (env.InitializedMarker is not null) File.AppendAllText(env.InitializedMarker, "INITIALIZED\n");
            if (env.PauseStdin) _paused = true;
            return;
        }
        if (method == "textDocument/didClose") return;
        if (method == "$/cancelRequest" && env.HonorCancel)
        {
            // Honor the cancellation by erroring the pending request id.
            if (message.Params is { } cancelParams
                && cancelParams.ValueKind == JsonValueKind.Object
                && cancelParams.TryGetProperty("id", out var cancelId)
                && cancelId.TryGetInt64(out var target))
            {
                Send(new JsonRpcMessage(Id: target, Error: JsonSerializer.SerializeToElement(new { code = -32800, message = "request cancelled" }, Options)));
            }
            return;
        }
        if (method?.StartsWith("textDocument/", StringComparison.Ordinal) == true)
        {
            if (env.Hang) return;
            void Reply()
            {
                if (env.ErrorReply)
                {
                    var error = new JsonObject { ["code"] = -32000 };
                    if (!env.ErrorNoMessage) error["message"] = "server refused the request";
                    Send(new JsonRpcMessage(Id: id, Error: JsonSerializer.SerializeToElement(error, Options)));
                }
                else
                {
                    var result = ResultFor(env, method!);
                    Send(new JsonRpcMessage(Id: id, Result: result is null ? NullElement() : JsonSerializer.SerializeToElement(result, Options)));
                }
                // Simulate an idle death: answer this request, then exit before the next one arrives.
                if (env.ExitAfterReply) _ = Task.Delay(20).ContinueWith(_ => Environment.Exit(0));
            }
            if (env.ReplyDelayMs > 0) _ = Task.Delay(env.ReplyDelayMs).ContinueWith(_ => Reply());
            else Reply();
            return;
        }
        // Unknown request with an id: answer null so the client never stalls.
        if (id is not null) Send(new JsonRpcMessage(Id: id, Result: NullElement()));
    }

    /// <summary>The JSON result for one request method; the hover slot echoes a named env variable when configured.</summary>
    private static JsonNode? ResultFor(FixtureEnv env, string method)
    {
        switch (method)
        {
            case "textDocument/definition": return env.Def;
            case "textDocument/references": return env.Refs;
            case "textDocument/implementation": return env.Impl;
            case "textDocument/hover":
                if (env.EchoEnv is not null)
                {
                    // LSP_FAKE_ECHO_ENV names a variable whose VALUE becomes the hover contents — a test
                    // can assert exactly what env reached this process.
                    var text = Environment.GetEnvironmentVariable(env.EchoEnv) ?? $"<{env.EchoEnv} unset>";
                    return new JsonObject { ["contents"] = text };
                }
                return env.Hover;
            default: return null;
        }
    }

    /// <summary>Emit a server→client request (or notification) when a didOpen arrives; the reply is logged to stderr.</summary>
    private static void EmitServerRequest(string kind)
    {
        if (kind == "notification")
        {
            Send(new JsonRpcMessage(Method: "window/logMessage", Params: JsonSerializer.SerializeToElement(new { type = 3, message = "hello" }, Options)));
            return;
        }
        var id = _serverRequestId++;
        var method = kind switch
        {
            "configuration" => "workspace/configuration",
            "applyEdit" => "workspace/applyEdit",
            "lifecycle" => "client/registerCapability",
            _ => "window/showMessageRequest",
        };
        var parameters = kind == "configuration"
            ? JsonSerializer.SerializeToElement(new { items = new object[] { new { section = "a" }, new { section = "b" } } }, Options)
            : JsonSerializer.SerializeToElement(new JsonObject(), Options);
        PendingServerRequests[id] = method;
        Send(new JsonRpcMessage(Id: id, Method: method, Params: parameters));
    }

    private static void Send(JsonRpcMessage message)
    {
        var bytes = Framing.EncodeMessage(message);
        lock (StdoutGate)
        {
            var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
    }

    /// <summary>Frame an arbitrary JSON value as one message (preamble items, including non-objects).</summary>
    private static void WriteFrame(JsonNode? value)
    {
        // JsonNode.Parse("null") and JsonValue.Create(null) both yield a C# null, so the serializer
        // (which renders a null node as the JSON null literal) is the reliable framing path.
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (StdoutGate)
        {
            var stdout = Console.OpenStandardOutput();
            stdout.Write(header, 0, header.Length);
            stdout.Write(body, 0, body.Length);
            stdout.Flush();
        }
    }

    private static void WriteRaw(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        lock (StdoutGate)
        {
            var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
    }

    /// <summary>Append one teardown event when the fixture is configured to expose process ordering.</summary>
    private static void MarkExit(FixtureEnv env, string eventName)
    {
        if (env.ExitMarker is not null) File.AppendAllText(env.ExitMarker, eventName + "\n");
    }

    private static void FloodStderr()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('E', 200));
        var stderr = Console.OpenStandardError();
        while (true)
        {
            stderr.Write(bytes, 0, bytes.Length);
            stderr.Flush();
            Thread.Sleep(5);
        }
    }

    /// <summary>Spawn a sleeping helper (itself) and record its pid; only the tree kill stops it.</summary>
    private static void SpawnHelper(string marker)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "FakeLspServer.dll");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(host) || !File.Exists(host)) host = "dotnet";
        var info = new ProcessStartInfo { FileName = host, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(dll);
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.Environment["LSP_FAKE_HELPER"] = "1";
        using var helper = Process.Start(info);
        if (helper is null) return;
        helper.StandardInput.Close();
        helper.StandardOutput.Close();
        helper.StandardError.Close();
        File.WriteAllText(marker, helper.Id.ToString());
    }

    private static void RegisterSignalHandlers(FixtureEnv env)
    {
        try
        {
            if (env.TrapSigterm)
            {
                _ = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => { });
            }
            else
            {
                _ = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
                {
                    MarkExit(env, "TERM");
                    Environment.Exit(0);
                });
            }
        }
        catch (PlatformNotSupportedException)
        {
            // Windows: the tree-kill escalation path never delivers SIGTERM.
        }
    }

    private static JsonElement NullElement() => JsonDocument.Parse("null").RootElement.Clone();

    private static JsonElement? Raw(JsonElement? element) => element;

    private static string? GetEnv(string name) => Environment.GetEnvironmentVariable(name);

    private static bool IsOne(string name) => Environment.GetEnvironmentVariable(name) == "1";

    private static int IntEnv(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static JsonNode? JsonEnv(string name)
        => Environment.GetEnvironmentVariable(name) is { } raw ? JsonNode.Parse(raw) : null;

    private sealed record FixtureEnv(
        string Enc,
        JsonNode? Sync,
        JsonNode? ExtraCaps,
        JsonNode? Def,
        JsonNode? Refs,
        JsonNode? Impl,
        JsonNode? Hover,
        string? EchoEnv,
        bool Hang,
        bool CrashOnOpen,
        bool ExitAfterReply,
        int ReplyDelayMs,
        string? OpenMarker,
        string? InitializedMarker,
        bool PauseStdin,
        int ExitDelayMs,
        string? ExitMarker,
        bool NoShutdown,
        string? OnOpen,
        bool ErrorReply,
        bool Garbage,
        bool BadLength,
        JsonNode? Preamble,
        bool ErrorNoMessage,
        bool HangInitialize,
        int ExitAfterMs,
        bool StderrFlood,
        bool EmojiStderr,
        bool HonorCancel,
        bool TrapSigterm,
        string? SpawnHelperMarker);
}
