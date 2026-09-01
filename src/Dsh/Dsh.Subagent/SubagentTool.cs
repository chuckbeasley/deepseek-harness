using System.Text.Json;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Subagent;

/// <summary>
/// The subagent delegation consumer (port of <c>@deepseek-ai/dsh-tool-subagent</c>, foreground
/// scope): delegate a self-contained task to the named provider and return the settled output.
/// Non-completed runs surface as tool failures with the exact stop-reason wording, the provider
/// diagnostic, and the preserved partial output — never counted as success. Background and
/// continuable modes are deferred with the jobs integration.
/// </summary>
public static class SubagentTool
{
    private const string Description =
        "Delegate a self-contained task to a subagent (a separate agent that works in its own context) "
        + "to offload focused, independent work — research, a scoped implementation, an analysis — "
        + "so it does not consume this conversation's context. The subagent returns its result, not "
        + "its intermediate steps. Give it a complete, standalone prompt: it does not see this conversation.";

    private const string ParametersSchema =
        "{\"description\":{\"type\":\"string\",\"required\":true,\"description\":\"A short (3-5 word) description of the delegated task, for display.\"},"
        + "\"prompt\":{\"type\":\"string\",\"required\":true,\"description\":\"The complete, self-contained task for the subagent. It does not share this conversation's context, so include everything it needs.\"}}";

    private const string OutputSchema =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{"
        + "\"runId\":{\"type\":\"string\",\"required\":true},"
        + "\"text\":{\"type\":\"string\",\"required\":true},"
        + "\"stopReason\":{\"type\":\"string\",\"required\":true},"
        + "\"diagnostic\":{\"type\":\"string\"}}}";

    /// <summary>Build the tool over the mounted subagent service and the named provider.</summary>
    public static ToolDefinition Definition(ISubagentService service, string providerName, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (providerName.Trim().Length == 0)
        {
            throw new ArgumentException("subagent tool: a provider name must be non-empty", nameof(providerName));
        }
        var name = toolName ?? "subagent";
        return new ToolDefinition(
            name,
            Description,
            JsonSerializer.Deserialize<JsonElement>(ParametersSchema),
            JsonSerializer.Deserialize<JsonElement>(OutputSchema),
            (args, context) => ExecuteAsync(service, providerName, name, args, context),
            Render: (_, value) =>
            {
                // The model-facing result is the child's final text (the recorded corpus shape).
                var text = value.TryGetProperty("text", out var textValue) ? textValue.GetString() ?? "" : "";
                return new ContentBlock[] { new TextBlock(text) };
            },
            PersistMeta: false);
    }

    private static async Task<JsonElement> ExecuteAsync(
        ISubagentService service, string providerName, string toolName, JsonElement args, ToolRunContext context)
    {
        var prompt = StringArg(args, "prompt", "invalid prompt: expected a non-empty string");
        var description = StringArg(args, "description", "invalid description: expected a non-empty string");
        // The parent session's route facts travel on the request so the in-process driver can
        // spawn the child loop under the recorded provider/model and session ancestry.
        var session = context.Session;
        var parentId = session?.Id.Value;
        var route = session is null
            ? (null, null)
            : session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header.Config).LastOrDefault() is { } config
                ? (config.Provider, config.Model)
                : (null, null);
        var run = await service.StartAsync(providerName, new SubagentRequest(
            prompt,
            description,
            ParentSessionId: parentId,
            ParentDelegationDepth: session?.Header.DelegationDepth,
            Provider: route.Item1,
            Model: route.Item2), context.CancellationToken)
            .ConfigureAwait(false);
        var result = await run.Result.ConfigureAwait(false);
        if (result.StopReason != SubagentStopReason.Completed)
        {
            throw new InvalidOperationException(FailureText(result));
        }
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["runId"] = run.Id.Value,
            ["text"] = result.Text,
            ["stopReason"] = StopReasonWire(result.StopReason),
        });
    }

    /// <summary>The exact stop-reason wording a non-completed run surfaces (never counted as success).</summary>
    private static string FailureText(SubagentResult result)
    {
        var message = result.StopReason switch
        {
            SubagentStopReason.Aborted => "subagent run was cancelled",
            SubagentStopReason.Error => "subagent run failed",
            SubagentStopReason.MaxTokens => "subagent run hit its token limit before finishing",
            SubagentStopReason.Refusal => "subagent declined the task",
            _ => $"subagent run ended abnormally ({StopReasonWire(result.StopReason)})",
        };
        if (result.Diagnostic is { Length: > 0 }) message += $"\nDiagnostic: {result.Diagnostic}";
        if (result.Text.Length > 0) message += $"\nPartial output before the run ended:\n{result.Text}";
        return message;
    }

    private static string StopReasonWire(SubagentStopReason reason) => reason switch
    {
        SubagentStopReason.Completed => "completed",
        SubagentStopReason.Aborted => "aborted",
        SubagentStopReason.Error => "error",
        SubagentStopReason.MaxTokens => "max-tokens",
        SubagentStopReason.Refusal => "refusal",
        _ => "unknown",
    };

    private static string StringArg(JsonElement args, string key, string error)
    {
        if (!args.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(error);
        }
        var text = value.GetString() ?? string.Empty;
        if (text.Trim().Length == 0) throw new ArgumentException(error);
        return text;
    }
}
