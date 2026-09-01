using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Harness.Llm;
using Harness.Session;
using Harness.Tools;

namespace Harness.Code;

/// <summary>
/// The <c>run_code</c> tool (port of the TS PTC code mode with the worker-thread runtime replaced
/// by the managed Jint engine — node is not used in the ported version): the program body runs as
/// one strict async function with the calling agent's visible tools bound as an awaitable
/// <c>tools</c> namespace; every binding call dispatches through the harness tool runtime and
/// records the <c>tool/code-dispatch-start</c>/<c>tool/code-dispatch</c> pairs; console levels are
/// captured; the completion value renders as the tool result (verbatim when a string, else
/// two-space JSON) prefixed by the captured logs.
/// </summary>
public static class RunCodeTool
{
    private const string ParametersSchema =
        "{\"code\":{\"type\":\"string\",\"required\":true,\"description\":\"The JavaScript program body (top-level await allowed; the harness tools are bound as the awaitable tools namespace).\"},"
        + "\"description\":{\"type\":\"string\",\"required\":true,\"description\":\"A short (3-5 word) description of what the program does.\"}}";

    private static readonly string[] ConsoleLevels = { "log", "info", "warn", "error", "debug" };

    /// <summary>Build the tool over the harness tool runtime that programmatic dispatches go through.</summary>
    public static ToolDefinition Definition(ToolRuntime tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return new ToolDefinition(
            Name: "run_code",
            Description: "Execute a JavaScript program with access to the harness tools and return its output.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchema)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}")!),
            Execute: (args, context) => ExecuteAsync(tools, args, context),
            Render: (_, value) => new ContentBlock[] { new TextBlock(value.GetString() ?? string.Empty) },
            PersistMeta: false);
    }

    private static async Task<JsonElement> ExecuteAsync(ToolRuntime tools, JsonElement args, ToolRunContext context)
    {
        var code = args.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? string.Empty : string.Empty;
        if (code.Trim().Length == 0) throw new ArgumentException("run_code: code must be a non-empty string");
        var description = args.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString() ?? string.Empty : string.Empty;
        if (description.Trim().Length == 0) throw new ArgumentException("run_code: description must be a non-empty string");
        var session = context.Session
            ?? throw new InvalidOperationException("run_code requires a calling session");
        var callId = context.CallId.Value;

        var engine = new Engine();
        var logs = new List<string>();
        var consoleShim = new Dictionary<string, object>();
        foreach (var level in ConsoleLevels)
        {
            consoleShim[level] = new Action<object?>(value => logs.Add(value?.ToString() ?? string.Empty));
        }
        engine.SetValue("console", consoleShim);

        var bridge = new Dictionary<string, object>();
        var dispatches = 0;
        foreach (var schema in tools.Schemas())
        {
            var name = schema.Name;
            bridge[name] = new Func<JsValue, Task<JsValue>>(async (jsArgs) =>
            {
                var n = Interlocked.Increment(ref dispatches);
                var subCallId = $"{callId}:code:{n}";
                var argumentsJson = JsonSerializer.Serialize(jsArgs.ToObject());
                using var arguments = JsonDocument.Parse(argumentsJson);
                var argumentsElement = arguments.RootElement.Clone();
                session.Append(new ToolCodeDispatchStartEvent
                {
                    RootCallId = callId,
                    ParentCallId = callId,
                    SubCallId = subCallId,
                    Name = name,
                    Arguments = argumentsElement,
                });
                var input = new ToolExecutionInput(new ToolCallId(subCallId), name, argumentsElement, context.CancellationToken)
                {
                    Session = session,
                };
                var result = await tools.ExecuteAsync(input, context.CancellationToken).ConfigureAwait(false);
                session.Append(new ToolCodeDispatchEvent
                {
                    RootCallId = callId,
                    ParentCallId = callId,
                    SubCallId = subCallId,
                    Name = name,
                    Arguments = argumentsElement,
                    IsError = result.IsError,
                    Content = result.Content,
                });
                if (result is ToolExecutionFailure failure)
                {
                    throw new InvalidOperationException(failure.Error.Message);
                }
                var value = ((ToolExecutionSuccess)result).Value;
                // JSON is a valid JavaScript expression; evaluating it rebuilds the value tree.
                return engine.Evaluate("(" + value.GetRawText() + ")");
            });
        }
        engine.SetValue("tools", bridge);

        JsValue completion;
        try
        {
            completion = await engine.EvaluateAsync("(async () => {\n'use strict';\n" + code + "\n})()", "run_code", context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"run_code program failed: {error.Message}");
        }
        var text = RenderResult(logs, completion, engine);
        return JsonSerializer.SerializeToElement(text);
    }

    /// <summary>Render the completion: captured logs (when any), then the value — verbatim for a string, else two-space JSON.</summary>
    internal static string RenderResult(IReadOnlyList<string> logs, JsValue completion, Engine engine)
    {
        var valueText = completion.IsString()
            ? completion.AsString()
            : completion.IsUndefined()
                ? string.Empty
                : PrettyJson(JsonSerializer.Serialize(completion.ToObject()));
        return logs.Count > 0 ? string.Join("\n", logs) + "\n" + valueText : valueText;
    }

    private static string PrettyJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            document.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}