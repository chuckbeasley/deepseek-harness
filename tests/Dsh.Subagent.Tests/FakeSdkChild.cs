using System.Text;
using System.Text.Json;

namespace Harness.Subagent.Tests;

/// <summary>
/// Scripted stand-in for the SDK runtime child, driven entirely by env vars — no model, no
/// network (port of the TS fake-runtime). Speaks the runtime's newline-delimited JSON-RPC
/// protocol on stdio: answers initialize, session/prompt (streaming scripted session.event and
/// session.status notifications, then the response), and shutdown. Entered via
/// <c>--fake-sdk-child</c> on the test assembly.
/// </summary>
public static class FakeSdkChild
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Run()
    {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        if (env.TryGetValue("FAKE_BOOT_MARKER", out var bootMarker) && bootMarker is { Length: > 0 })
        {
            File.WriteAllText(bootMarker, "boot\n");
        }
        if (env.TryGetValue("FAKE_STDERR", out var stderrLine))
        {
            Console.Error.WriteLine(stderrLine);
        }
        if (env.ContainsKey("FAKE_EXIT_BEFORE_INIT")) return 3;

        var input = Console.In;
        var output = Console.Out;
        var seq = 0;
        foreach (var line in ReadLines(input))
        {
            if (line.Length == 0) continue;
            Frame frame;
            try
            {
                frame = JsonSerializer.Deserialize<Frame>(line, WireJson) ?? throw new JsonException("empty");
            }
            catch (JsonException)
            {
                continue;
            }
            if (frame.Method is null || frame.Id is not long id) continue;
            switch (frame.Method)
            {
                case "initialize":
                    if (env.TryGetValue("FAKE_RECORD_INIT", out var recordPath) && recordPath is { Length: > 0 })
                    {
                        File.AppendAllText(recordPath, JsonSerializer.Serialize(frame.Params, WireJson) + "\n");
                    }
                    if (env.ContainsKey("FAKE_HANG_INIT")) break;
                    if (env.TryGetValue("FAKE_INIT_READY", out var ready) && env.TryGetValue("FAKE_INIT_GO", out var go))
                    {
                        File.WriteAllText(ready, "ready\n");
                        WaitFor(go);
                        Respond(output, id, new { serverInfo = new { name = "deepseek-harness-sdk-runtime", version = "0.0.1" } });
                        break;
                    }
                    if (env.ContainsKey("FAKE_INIT_ERROR"))
                    {
                        Error(output, id, 7, "scripted init failure");
                        break;
                    }
                    if (env.ContainsKey("FAKE_MALFORMED"))
                    {
                        Respond(output, id, new { });
                        break;
                    }
                    Respond(output, id, new { serverInfo = new { name = "deepseek-harness-sdk-runtime", version = "0.0.1" } });
                    break;
                case "session/prompt":
                    var sessionId = ParamsString(frame.Params, "sessionId");
                    if (env.ContainsKey("FAKE_HANG_PROMPT")) break;
                    if (env.ContainsKey("FAKE_STREAM_THEN_MALFORMED"))
                    {
                        Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "assistant/chunk", new { turn = 0, step = 0, chunk = new { type = "text-delta", index = 0, text = "streamed then cut short" } }) });
                        Respond(output, id, new { });
                        break;
                    }
                    if (env.ContainsKey("FAKE_EXIT_DURING_PROMPT"))
                    {
                        var partial = EnvString(env, "FAKE_TEXT") ?? "partial before exit";
                        Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "assistant/chunk", new { turn = 0, step = 0, chunk = new { type = "text-delta", index = 0, text = partial } }) });
                        Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "assistant/message", Message(seq, partial, env)) });
                        Respond(output, id, new { messageId = "fake-user-" + seq });
                        Console.Out.Flush();
                        Environment.Exit(17);
                        break;
                    }
                    if (env.ContainsKey("FAKE_MALFORMED") || env.ContainsKey("FAKE_MALFORMED_PROMPT"))
                    {
                        Respond(output, id, new { });
                        break;
                    }
                    RunTurn(output, ref seq, sessionId, env);
                    Notify(output, "session.status", new { sessionId, status = "idle" });
                    Respond(output, id, new { messageId = "fake-user-" + seq });
                    break;
                case "shutdown":
                    Respond(output, id, new { });
                    output.Flush();
                    return 0;
            }
        }
        return 0;
    }

    private static void RunTurn(TextWriter output, ref int seq, string sessionId, IReadOnlyDictionary<string, string?> env)
    {
        var text = AssistantText(env);
        Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "turn/start", new { turn = 0 }) });
        Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "assistant/chunk", new { turn = 0, step = 0, chunk = new { type = "text-delta", index = 0, text } }) });
        Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "assistant/message", Message(seq, text, env)) });
        var reasonKind = EnvString(env, "FAKE_REASON_KIND") ?? "completed";
        if (reasonKind != "none")
        {
            var reason = reasonKind == "aborted"
                ? (object)new { kind = "aborted", reason = new { kind = EnvString(env, "FAKE_ABORT_REASON_KIND") ?? "user" } }
                : reasonKind == "error"
                    ? new { kind = "error", error = new { message = "scripted child error", code = "UNKNOWN" } }
                    : new { kind = reasonKind };
            Notify(output, "session.event", new { sessionId, @event = Event(ref seq, "turn/end", new { turn = 0, reason }) });
        }
    }

    private static string AssistantText(IReadOnlyDictionary<string, string?> env)
    {
        var parts = new List<string>();
        if (env.ContainsKey("FAKE_ECHO_CWD")) parts.Add("cwd=" + Environment.CurrentDirectory);
        foreach (var name in (EnvString(env, "FAKE_ECHO_ENV") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            parts.Add($"{name}={env.GetValueOrDefault(name)}");
        }
        parts.Add(EnvString(env, "FAKE_TEXT") ?? "hello from fake runtime");
        return string.Join("\n", parts);
    }

    private static object Message(int seq, string text, IReadOnlyDictionary<string, string?> env)
        => new
        {
            turn = 0,
            step = 0,
            message = new
            {
                id = "fake-assistant-" + seq,
                role = "assistant",
                content = env.ContainsKey("FAKE_EMPTY_MESSAGE") ? Array.Empty<object>() : new object[] { new { type = "text", text } },
                source = new { kind = "model", provider = "fake", model = "fake" },
            },
        };

    private static object Event(ref int seq, string type, object data) => new { type, seq = seq++, time = 0, data };

    private static IEnumerable<string> ReadLines(TextReader input)
    {
        while (true)
        {
            var line = input.ReadLine();
            if (line is null) yield break;
            yield return line;
        }
    }

    private static void Notify(TextWriter output, string method, object parameters)
    {
        output.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }, WireJson));
        output.Flush();
    }

    private static void Respond(TextWriter output, long id, object result)
    {
        output.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, WireJson));
        output.Flush();
    }

    private static void Error(TextWriter output, long id, int code, string message)
    {
        output.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, WireJson));
        output.Flush();
    }

    private static string? ParamsString(JsonElement? parameters, string key)
        => parameters is JsonElement element
            && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? EnvString(IReadOnlyDictionary<string, string?> env, string name)
        => env.TryGetValue(name, out var value) ? value : null;

    private static void WaitFor(string path)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!File.Exists(path))
        {
            if (Environment.TickCount64 > deadline) return;
            Thread.Sleep(5);
        }
    }

    private sealed class Frame
    {
        public long? Id { get; set; }

        public string? Method { get; set; }

        public JsonElement? Params { get; set; }
    }
}
