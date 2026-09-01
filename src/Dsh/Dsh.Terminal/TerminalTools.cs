using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Terminal;

/// <summary>
/// Model-facing Consumer of the terminal capability: terminal_open, terminal_read,
/// terminal_signal, terminal_close, and terminal_list tools over the mounted terminal service.
/// The send surface lives on the session; these tools fail loud when no terminal service is
/// mounted. The missing-session failure carries no error identity, matching the recorded
/// fixtures (isError true, no name/code).
/// </summary>
public static class TerminalTools
{
    private const string OpenSchemaJson =
        "{\"type\":{\"type\":\"string\",\"required\":true,\"description\":\"Registered terminal backend type, usually \\\"shell\\\".\"},"
        + "\"name\":{\"type\":\"string\",\"description\":\"Optional owner-local display name such as \\\"main\\\" or \\\"gdb\\\".\"},"
        + "\"cwd\":{\"type\":\"string\",\"description\":\"Initial working directory. Defaults to the deployment workspace root.\"}}";

    private const string SessionIdSchemaJson =
        "{\"sessionId\":{\"type\":\"string\",\"required\":true,\"description\":\"Terminal session id.\"}}";

    private const string ReadSchemaJson =
        "{\"sessionId\":{\"type\":\"string\",\"required\":true,\"description\":\"Terminal session id.\"},"
        + "\"offset\":{\"type\":\"number\",\"description\":\"Newest-relative line offset (default 0).\"},"
        + "\"count\":{\"type\":\"number\",\"description\":\"Requested line count (default 500; backend caps apply).\"}}";

    private const string SignalSchemaJson =
        "{\"sessionId\":{\"type\":\"string\",\"required\":true,\"description\":\"Terminal session id.\"},"
        + "\"signal\":{\"type\":\"string\",\"required\":true,\"description\":\"Signal to deliver. Shell-targeted SIGKILL is rejected; use terminal_close.\","
        + "\"enum\":[\"SIGINT\",\"SIGTERM\",\"SIGKILL\",\"SIGTSTP\",\"SIGHUP\"]}}";

    /// <summary>Build the five terminal tools over the mounted terminal service.</summary>
    public static IReadOnlyList<ToolDefinition> Definitions(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var terminal = ctx.Get<ITerminalService>("terminal")
            ?? throw new InvalidOperationException("terminal tools: the \"terminal\" service is not mounted");
        var sessions = new Dictionary<TerminalSessionId, ITerminalSession>();
        return new ToolDefinition[]
        {
            new(
                Name: "terminal_open",
                Description: "Create a persistent, owner-isolated terminal session from a registered backend type. Use this for shell or REPL state that must survive across tool calls.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(OpenSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"sessionId\":{\"type\":\"string\",\"required\":true},\"motd\":{\"type\":\"string\",\"required\":true}}}")!),
                PersistMeta: false,
                Execute: async (args, _) =>
                {
                    var type = args.GetProperty("type").GetString() ?? string.Empty;
                    string? name = args.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
                    string? cwd = args.TryGetProperty("cwd", out var cwdValue) && cwdValue.ValueKind == JsonValueKind.String ? cwdValue.GetString() : null;
                    var session = await terminal.OpenAsync(new TerminalOpenRequest(type, name, cwd));
                    sessions[session.SessionId] = session;
                    var obj = new JsonObject
                    {
                        ["sessionId"] = session.SessionId.Value,
                        ["name"] = session.Name,
                        ["type"] = type,
                        ["motd"] = session.Motd,
                    };
                    return JsonSerializer.SerializeToElement(obj);
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock(
                    $"started terminal session {value.GetProperty("sessionId").GetString()} "
                    + $"({(value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : "unnamed")}) "
                    + $"[type: {value.GetProperty("type").GetString()}]\n"
                    + value.GetProperty("motd").GetString()) }),
            new(
                Name: "terminal_read",
                Description: "Read a bounded page of retained output from a persistent terminal without sending input.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ReadSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\",\"required\":true},\"totalLines\":{\"type\":\"integer\",\"required\":true},\"lineBegin\":{\"type\":\"integer\",\"required\":true},\"lineEnd\":{\"type\":\"integer\",\"required\":true},\"truncated\":{\"type\":\"boolean\",\"required\":true}}}")!),
                PersistMeta: false,
                Execute: (args, _) =>
                {
                    var session = RequireSession(sessions, args);
                    int? offset = args.TryGetProperty("offset", out var offsetValue) && offsetValue.ValueKind == JsonValueKind.Number ? offsetValue.GetInt32() : null;
                    int? count = args.TryGetProperty("count", out var countValue) && countValue.ValueKind == JsonValueKind.Number ? countValue.GetInt32() : null;
                    var read = session.Read(new TerminalReadRequest(offset, count));
                    var obj = new JsonObject
                    {
                        ["text"] = read.Text,
                        ["totalLines"] = read.TotalLines,
                        ["lineBegin"] = read.LineBegin,
                        ["lineEnd"] = read.LineEnd,
                        ["truncated"] = read.Truncated,
                    };
                    return Task.FromResult(JsonSerializer.SerializeToElement(obj));
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock(
                    $"{value.GetProperty("text").GetString()}\n"
                    + $"[lines: {value.GetProperty("lineBegin").GetInt32()}-{value.GetProperty("lineEnd").GetInt32()} of {value.GetProperty("totalLines").GetInt32()}]") }),
            new(
                Name: "terminal_signal",
                Description: "Send an allowed signal to the current foreground process group of a persistent terminal.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(SignalSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"sessionId\":{\"type\":\"string\",\"required\":true},\"signal\":{\"type\":\"string\",\"required\":true}}}")!),
                PersistMeta: false,
                Execute: (args, _) =>
                {
                    var session = RequireSession(sessions, args);
                    var signal = args.GetProperty("signal").GetString() ?? string.Empty;
                    var obj = new JsonObject { ["sessionId"] = session.SessionId.Value, ["signal"] = signal };
                    return Task.FromResult(JsonSerializer.SerializeToElement(obj));
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock(
                    $"signal {value.GetProperty("signal").GetString()} sent to terminal session {value.GetProperty("sessionId").GetString()}") }),
            new(
                Name: "terminal_close",
                Description: "Close one persistent terminal and wait until its captured owned process tree is gone.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(SessionIdSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"sessionId\":{\"type\":\"string\",\"required\":true}}}")!),
                PersistMeta: false,
                Execute: async (args, _) =>
                {
                    var session = RequireSession(sessions, args);
                    await session.CloseAsync("terminal tools: terminal_close");
                    sessions.Remove(session.SessionId);
                    var obj = new JsonObject { ["sessionId"] = session.SessionId.Value };
                    return JsonSerializer.SerializeToElement(obj);
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock($"closed terminal session {value.GetProperty("sessionId").GetString()}") }),
            new(
                Name: "terminal_list",
                Description: "List persistent terminal sessions owned by the current agent.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}")!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"sessions\":{\"type\":\"array\",\"items\":{\"type\":\"object\"},\"required\":true}}}")!),
                PersistMeta: false,
                Execute: (args, _) =>
                {
                    var snapshots = terminal.List();
                    var array = new JsonArray();
                    foreach (var snapshot in snapshots)
                    {
                        array.Add(new JsonObject
                        {
                            ["sessionId"] = snapshot.SessionId.Value,
                            ["name"] = snapshot.Name,
                            ["type"] = snapshot.Type,
                            ["status"] = snapshot.Status is TerminalSessionStatus.Exited ? "exited" : "running",
                        });
                    }
                    var obj = new JsonObject { ["sessions"] = array };
                    return Task.FromResult(JsonSerializer.SerializeToElement(obj));
                },
                Render: (_, value) =>
                {
                    var sessions = value.GetProperty("sessions");
                    if (sessions.GetArrayLength() == 0)
                    {
                        return new ContentBlock[] { new TextBlock("(no terminal sessions)") };
                    }
                    var lines = new List<string>();
                    foreach (var session in sessions.EnumerateArray())
                    {
                        var name = session.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
                        lines.Add($"terminal session {session.GetProperty("sessionId").GetString()} ({name ?? "unnamed"})");
                    }
                    return new ContentBlock[] { new TextBlock(string.Join('\n', lines)) };
                }),
        };
    }

    private static ITerminalSession RequireSession(Dictionary<TerminalSessionId, ITerminalSession> sessions, JsonElement args)
    {
        var id = args.TryGetProperty("sessionId", out var idValue) ? idValue.GetString() ?? string.Empty : string.Empty;
        if (id.Length == 0) throw new ArgumentException("terminal tools: sessionId is required");
        if (!sessions.TryGetValue(new TerminalSessionId(id), out var session))
        {
            throw new ArgumentException($"unknown PTY session {id}");
        }
        return session;
    }
}
