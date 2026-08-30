using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Terminal;

/// <summary>
/// Model-facing Consumer of the terminal capability: terminal_open, terminal_send, and
/// terminal_read tools over the mounted terminal service. The send tool resolves through the
/// service's read surface; all three fail loud when no terminal service is mounted.
/// </summary>
public static class TerminalTools
{
    private const string OpenSchemaJson =
        "{\"type\":{\"type\":\"string\",\"required\":true,\"description\":\"The terminal backend type (\\\"local\\\").\"},"
        + "\"name\":{\"type\":\"string\",\"description\":\"Optional owner-local display name.\"},"
        + "\"cwd\":{\"type\":\"string\",\"description\":\"Optional initial working directory.\"}}";

    private const string SendSchemaJson =
        "{\"sessionId\":{\"type\":\"string\",\"required\":true,\"description\":\"The terminal session id from terminal_open.\"},"
        + "\"text\":{\"type\":\"string\",\"required\":true,\"description\":\"Text to write to the terminal.\"},"
        + "\"submit\":{\"type\":\"boolean\",\"required\":true,\"description\":\"Whether to write the Enter sequence after the text.\"}}";

    private const string ReadSchemaJson =
        "{\"sessionId\":{\"type\":\"string\",\"required\":true,\"description\":\"The terminal session id from terminal_open.\"}}";

    /// <summary>Build the three terminal tools over the mounted terminal service.</summary>
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
                Description: "Open a persistent terminal session and return its session id.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(OpenSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"sessionId\":{\"type\":\"string\",\"required\":true},\"motd\":{\"type\":\"string\",\"required\":true}}}")!),
                Execute: async (args, _) =>
                {
                    var type = args.GetProperty("type").GetString() ?? string.Empty;
                    string? name = args.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
                    string? cwd = args.TryGetProperty("cwd", out var cwdValue) && cwdValue.ValueKind == JsonValueKind.String ? cwdValue.GetString() : null;
                    var session = await terminal.OpenAsync(new TerminalOpenRequest(type, name, cwd));
                    sessions[session.SessionId] = session;
                    var obj = new JsonObject { ["sessionId"] = session.SessionId.Value, ["motd"] = session.Motd };
                    return JsonSerializer.SerializeToElement(obj);
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock($"terminal session {value.GetProperty("sessionId").GetString()} opened") }),
            new(
                Name: "terminal_send",
                Description: "Write text to a terminal session and return the resulting viewport.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(SendSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"viewport\":{\"type\":\"string\",\"required\":true},\"waitReason\":{\"type\":\"string\",\"required\":true}}}")!),
                Execute: async (args, context) =>
                {
                    var session = RequireSession(sessions, args);
                    var text = args.GetProperty("text").GetString() ?? string.Empty;
                    var submit = args.GetProperty("submit").GetBoolean();
                    var operation = session.StartSend(new TerminalSendRequest(text, submit));
                    var result = await operation.Done;
                    var obj = new JsonObject { ["viewport"] = result.Viewport, ["waitReason"] = result.WaitReason.ToString() };
                    return JsonSerializer.SerializeToElement(obj);
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock(value.GetProperty("viewport").GetString() ?? string.Empty) }),
            new(
                Name: "terminal_read",
                Description: "Read a bounded page from a terminal session's retained scrollback.",
                Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ReadSchemaJson)!),
                OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\",\"required\":true},\"totalLines\":{\"type\":\"integer\",\"required\":true},\"truncated\":{\"type\":\"boolean\",\"required\":true}}}")!),
                Execute: (args, _) =>
                {
                    var session = RequireSession(sessions, args);
                    var read = session.Read(new TerminalReadRequest());
                    var obj = new JsonObject { ["text"] = read.Text, ["totalLines"] = read.TotalLines, ["truncated"] = read.Truncated };
                    return Task.FromResult(JsonSerializer.SerializeToElement(obj));
                },
                Render: (_, value) => new ContentBlock[] { new TextBlock(value.GetProperty("text").GetString() ?? string.Empty) }),
        };
    }

    private static ITerminalSession RequireSession(Dictionary<TerminalSessionId, ITerminalSession> sessions, JsonElement args)
    {
        var id = args.TryGetProperty("sessionId", out var idValue) ? idValue.GetString() ?? string.Empty : string.Empty;
        if (id.Length == 0) throw new ArgumentException("terminal tools: sessionId is required");
        if (!sessions.TryGetValue(new TerminalSessionId(id), out var session))
        {
            throw new ArgumentException($"terminal tools: unknown session \"{id}\"");
        }
        return session;
    }
}
