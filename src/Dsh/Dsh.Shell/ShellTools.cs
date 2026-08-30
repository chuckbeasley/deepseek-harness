using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Shell;

/// <summary>
/// Model-facing Consumer of the shell capability: the <c>bash</c> tool (port of the tool-bash
/// surface minus <c>run_in_background</c> and <c>sandbox_permissions</c>, which belong to the jobs
/// and sandbox seams of later waves). Each call runs in a fresh shell; non-zero exits resolve as
/// results carrying <c>[exit code: N]</c> markers rather than failures.
/// </summary>
public static class ShellTools
{
    private const string ParametersSchemaJson =
        "{\"command\":{\"type\":\"string\",\"required\":true,\"description\":\"The shell command to execute.\"},"
        + "\"description\":{\"type\":\"string\",\"required\":true,\"description\":\"Clear, concise description of what this command does in active voice, 5-10 words (shown in the UI).\"},"
        + "\"timeoutMs\":{\"type\":\"number\",\"description\":\"Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry.\"},"
        + "\"workdir\":{\"type\":\"string\",\"description\":\"Working directory for this command. Defaults to the executor-configured workspace; a relative path is resolved against it.\"}}";

    private const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{"
        + "\"kind\":{\"type\":\"string\",\"required\":true,\"const\":\"foreground\"},"
        + "\"exitCode\":{\"required\":true,\"oneOf\":[{\"type\":\"integer\"},{\"type\":\"null\"}]},"
        + "\"signal\":{\"required\":true,\"oneOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
        + "\"timedOut\":{\"type\":\"boolean\",\"required\":true},"
        + "\"aborted\":{\"type\":\"boolean\",\"required\":true},"
        + "\"timeoutMs\":{\"type\":\"number\",\"required\":true},"
        + "\"stdout\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{\"text\":{\"type\":\"string\",\"required\":true},\"truncated\":{\"type\":\"boolean\",\"required\":true},\"spillPath\":{\"type\":\"string\"}}},"
        + "\"stderr\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{\"text\":{\"type\":\"string\",\"required\":true},\"truncated\":{\"type\":\"boolean\",\"required\":true},\"spillPath\":{\"type\":\"string\"}}}}}";

    private const string Description =
        "Execute a shell command and return its stdout/stderr. Each call runs in a fresh shell: no state "
        + "(cwd, variables, functions) persists between calls — pass `workdir` instead of using `cd`. "
        + "Non-zero exits are reported as `[exit code: N]`. Long output is truncated to its tail; the full "
        + "output is saved to a file whose path is reported when available.";

    /// <summary>Build the <c>bash</c> ToolDefinition over the mounted shell service.</summary>
    public static ToolDefinition Definition(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var shell = ctx.Get<IShellService>("shell")
            ?? throw new InvalidOperationException("shell tool: the \"shell\" service is not mounted");
        return new ToolDefinition(
            Name: "bash",
            Description: Description,
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var request = ParseArguments(args);
                var spec = shell.Resolve(new ShellExecRequest(
                    request.Command,
                    request.Workdir,
                    request.TimeoutMs,
                    CancellationToken: context.CancellationToken));
                var result = shell.Run(spec);
                return Task.FromResult(ToResultJson(result));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderValue(value)) });
    }

    private sealed record BashToolArgs(string Command, string Description, string? Workdir, int? TimeoutMs);

    private static BashToolArgs ParseArguments(JsonElement args)
    {
        var command = args.TryGetProperty("command", out var commandValue) ? commandValue.GetString() ?? string.Empty : string.Empty;
        var description = args.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString() ?? string.Empty : string.Empty;
        if (command.Trim().Length == 0)
        {
            throw new ArgumentException("invalid command: expected a non-empty string");
        }
        if (description.Trim().Length == 0)
        {
            throw new ArgumentException("invalid description: expected a non-empty string");
        }
        string? workdir = null;
        if (args.TryGetProperty("workdir", out var workdirValue) && workdirValue.ValueKind == JsonValueKind.String)
        {
            workdir = workdirValue.GetString();
        }
        int? timeoutMs = null;
        if (args.TryGetProperty("timeoutMs", out var timeoutValue) && timeoutValue.ValueKind == JsonValueKind.Number)
        {
            timeoutMs = timeoutValue.GetInt32();
            if (timeoutMs <= 0)
            {
                throw new ArgumentException($"invalid timeoutMs: expected a positive number, got {timeoutMs}");
            }
        }
        return new BashToolArgs(command, description, workdir, timeoutMs);
    }

    private static JsonElement ToResultJson(ShellRunResult result)
    {
        var obj = new JsonObject
        {
            ["kind"] = "foreground",
            ["exitCode"] = result.ExitCode is null ? null : JsonValue.Create(result.ExitCode.Value),
            ["signal"] = result.Signal,
            ["timedOut"] = result.TimedOut,
            ["aborted"] = result.Aborted,
            ["timeoutMs"] = result.TimeoutMs,
            ["stdout"] = StreamJson(result.Stdout),
            ["stderr"] = StreamJson(result.Stderr),
        };
        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonObject StreamJson(Subprocess.CollectedOutput output)
    {
        var obj = new JsonObject
        {
            ["text"] = output.Text,
            ["truncated"] = output.Truncated,
        };
        if (output.SpillPath is not null) obj["spillPath"] = output.SpillPath;
        return obj;
    }

    private static string RenderValue(JsonElement value)
    {
        var stdout = StreamJsonOf(value.GetProperty("stdout"));
        var stderr = StreamJsonOf(value.GetProperty("stderr"));
        var result = new ShellRunResult(
            value.GetProperty("exitCode").ValueKind == JsonValueKind.Null ? null : value.GetProperty("exitCode").GetInt32(),
            value.GetProperty("signal").ValueKind == JsonValueKind.Null ? null : value.GetProperty("signal").GetString(),
            value.GetProperty("timedOut").GetBoolean(),
            value.GetProperty("aborted").GetBoolean(),
            value.GetProperty("timeoutMs").GetInt32(),
            stdout,
            stderr);
        return ShellRender.RenderResult(result);
    }

    private static Subprocess.CollectedOutput StreamJsonOf(JsonElement element)
        => new(
            element.GetProperty("text").GetString() ?? string.Empty,
            element.GetProperty("truncated").GetBoolean(),
            element.TryGetProperty("spillPath", out var spill) && spill.ValueKind == JsonValueKind.String ? spill.GetString() : null);
}
