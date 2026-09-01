using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cordis.Core;
using Harness.Agent;
using Harness.AgentLoop;
using AgentLoopService = Harness.AgentLoop.AgentLoop;
using Harness.Llm;
using Harness.Session;
using Harness.Subagent;
using Harness.Tools;

namespace Harness.Workflow;

/// <summary>
/// The model-facing Ralph consumer plus the structured-output child tool (port of
/// <c>@deepseek-ai/dsh-tool-ralph</c> with the fixed orchestration written natively: the TS runs
/// one workflow script per call; this port runs the same round loop in C# — one fresh one-shot
/// child per round carrying only the immutable objective and the previous bounded handoff, and
/// the final terminal envelope rendered from the last structured report).
/// </summary>
public static class RalphTool
{
    /// <summary>The delegation-depth ceiling for Ralph children (the TS subagent default).</summary>
    public const int MaxDelegationDepth = 2;

    /// <summary>Deployment ceiling for one call's round count (the TS default).</summary>
    public const int DefaultMaxRounds = 256;

    /// <summary>Maximum serialized characters in one structured handoff (the TS default).</summary>
    public const int MaxHandoffChars = 16_384;

    private const string RalphDescription =
        "Run a foreground fresh-agent Ralph loop toward one immutable objective. "
        + "Use only when the direct human explicitly asks for Ralph or fresh-agent iteration. Each round "
        + "opens a new child with no parent conversation or prior child session; the shared workspace is "
        + "long-term memory, and only a bounded structured report crosses rounds. The call returns when "
        + "a worker reports completion or a concrete blocker, or at the round limit. Ordinary long-running same-session work "
        + "belongs to goal tools.";

    private const string ParametersSchema =
        "{\"objective\":{\"type\":\"string\",\"required\":true,\"description\":\"The immutable completion objective for every fresh Ralph round.\"},"
        + "\"maxRounds\":{\"type\":\"number\",\"description\":\"Optional positive safe-integer round cap, bounded by the deployment ceiling.\"}}";

    private const string StructuredOutputSchema =
        "{\"status\":{\"type\":\"string\",\"required\":true,\"enum\":[\"continue\",\"complete\",\"blocked\"]},"
        + "\"summary\":{\"type\":\"string\",\"required\":true},"
        + "\"evidence\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"required\":true},"
        + "\"nextSteps\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"required\":true},"
        + "\"blocker\":{\"type\":\"string\",\"required\":true}}";

    /// <summary>Build the <c>structured_output</c> tool a Ralph child uses to record its round report.</summary>
    public static ToolDefinition StructuredOutputDefinition()
        => new(
            Name: "structured_output",
            Description: "Record one structured output value for the orchestrating workflow.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(StructuredOutputSchema)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}")!),
            Execute: (args, _) =>
            {
                var report = args.ValueKind == JsonValueKind.Object
                    ? JsonObject.Create(args) ?? new JsonObject()
                    : new JsonObject();
                ValidateReport(report);
                return Task.FromResult(JsonSerializer.SerializeToElement(report));
            },
            Render: (_, _) => new ContentBlock[] { new TextBlock("Structured output recorded.") },
            PersistMeta: false);

    /// <summary>Build the <c>ralph</c> tool over the agent loop used to spawn the fresh children.</summary>
    public static ToolDefinition Definition(AgentLoopService loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return new ToolDefinition(
            Name: "ralph",
            Description: RalphDescription,
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchema)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}")!),
            Execute: (args, context) => ExecuteAsync(loop, args, context),
            Render: (_, value) => new ContentBlock[] { new TextBlock(value.GetString() ?? string.Empty) },
            PersistMeta: false);
    }

    /// <summary>
    /// Install the child descriptor listener: a child whose AgentOptions names a subagent provider
    /// records the durable <c>subagent/descriptor</c> once, before its first step/start.
    /// </summary>
    public static IDisposable InstallDescriptorListener(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.On(LoopEvents.PreStep,
            new Func<PreStepProposal, Func<Task<PreStepDecision>>, Task<PreStepDecision>>(async (proposal, next) =>
            {
                var downstream = await next();
                var provider = proposal.Agent.Options.SubagentProvider;
                if (provider is { Length: > 0 }
                    && !proposal.Agent.Session.Events.Any(evt => evt is SubagentDescriptorEvent))
                {
                    proposal.Agent.Session.Append(new SubagentDescriptorEvent
                    {
                        Version = 3,
                        Mode = "one-shot",
                        Provider = provider,
                    });
                }
                return downstream;
            }));
    }

    private static async Task<JsonElement> ExecuteAsync(AgentLoopService loop, JsonElement args, ToolRunContext context)
    {
        var objective = args.TryGetProperty("objective", out var objectiveValue) && objectiveValue.ValueKind == JsonValueKind.String
            ? (objectiveValue.GetString() ?? string.Empty).Trim()
            : string.Empty;
        if (objective.Length == 0) throw new ArgumentException("Ralph objective must be a non-empty string");
        var maxRounds = ResolveMaxRounds(
            args.TryGetProperty("maxRounds", out var roundsValue) && roundsValue.ValueKind == JsonValueKind.Number
                ? roundsValue.GetInt32()
                : (int?)null);

        var session = context.Session;
        var parentId = session?.Id.Value;
        var (provider, model) = session is null
            ? (null, null)
            : session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header.Config).LastOrDefault() is { } config
                ? (config.Provider, config.Model)
                : (null, null);
        var depth = (session?.Header.DelegationDepth ?? 0) + 1;
        if (depth > MaxDelegationDepth)
        {
            throw new InvalidOperationException($"subagent depth {depth} exceeds maxDepth {MaxDelegationDepth}");
        }

        JsonObject? previous = null;
        for (var round = 1; round <= maxRounds; round++)
        {
            var prior = previous is null
                ? "(none — this is the first round)"
                : JsonSerializer.Serialize(previous);
            var prompt = BuildPrompt(objective, round, maxRounds, prior);
            var child = await RunRoundAsync(loop, provider, model, depth, parentId, prompt, context.CancellationToken);
            previous = child;
            var status = child["status"]?.GetValue<string>();
            if (status == "complete") return JsonSerializer.SerializeToElement(RenderResult("complete", round, child));
            if (status == "blocked") return JsonSerializer.SerializeToElement(RenderResult("blocked", round, child));
        }
        return JsonSerializer.SerializeToElement(RenderResult("budget-limited", maxRounds, previous ?? new JsonObject()));
    }

    /// <summary>Spawn one fresh one-shot child, run its single turn, and read its structured round report.</summary>
    private static async Task<JsonObject> RunRoundAsync(
        AgentLoopService loop, string? provider, string? model, int depth, string? parentId, string prompt, CancellationToken ct)
    {
        var sessionId = new SessionId(Guid.NewGuid().ToString("D"));
        var options = new AgentOptions
        {
            Provider = provider,
            Model = model,
            Cwd = Environment.CurrentDirectory,
            DelegationDepth = depth,
            ParentSessionId = parentId,
            Origin = "subagent",
            SubagentProvider = "spawn",
        };
        var handle = loop.Create(sessionId, options, source: "subagent");
        try
        {
            var driver = loop.GetLoop(sessionId)
                ?? throw new InvalidOperationException("ralph: the child loop was not published");
            var message = new UserMessage
            {
                Id = new MessageId(Guid.NewGuid().ToString("D")),
                Content = new ContentBlock[] { new TextBlock(prompt) },
                Source = new UserSource(),
            };
            driver.Send(message, InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var child = handle.Agent.Session;
            var call = child.Events.OfType<AssistantMessageEvent>()
                .SelectMany(evt => evt.Message.Content.OfType<ToolCallBlock>())
                .FirstOrDefault(block => block.Name == "structured_output");
            if (call is null)
            {
                throw new InvalidOperationException("Ralph child returned no structured round report");
            }
            using var document = JsonDocument.Parse(call.Arguments);
            // Detach a deep copy: JsonNode views created over the document share its lifetime.
            return ValidateReport(JsonNode.Parse(document.RootElement.GetRawText())!.AsObject());
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>The fixed fresh-worker prompt for one round (verbatim port of the TS script's prompt).</summary>
    internal static string BuildPrompt(string objective, int round, int maxRounds, string prior)
        => string.Join("\n\n", new[]
        {
            "You are one fresh worker in a foreground Ralph loop. You receive no parent conversation and no prior child session. Do not call the ralph tool: this round already is its worker.",
            "Immutable objective:\n" + objective,
            $"Ralph round: {round} of {maxRounds}.",
            "The shared workspace and its current working tree are the long-term memory and source of truth. Inspect them before acting, preserve existing work, perform concrete in-scope work, and verify what you change. Treat the previous report only as a bounded handoff; confirm it against the workspace.",
            "Previous structured handoff:\n" + prior,
            "Return one report with exact normalized strings. Use status continue with at least one nextSteps entry while useful work remains; complete only with concrete evidence and no nextSteps; blocked only when no meaningful progress is possible without human input or an external-state change. blocker must be empty unless blocked.",
        });

    /// <summary>Test hook: validate one structured report, throwing on the recorded failure classes.</summary>
    internal static bool ValidateReportForTest(JsonObject report)
    {
        ValidateReport(report);
        return true;
    }

    /// <summary>Test hook: render the terminal envelope for one run outcome.</summary>
    internal static string RenderResultForTest(string status, int roundsStarted, JsonObject report)
        => RenderResult(status, roundsStarted, report);

    /// <summary>Validate one structured round report (port of the TS readReport/validateReport rules).</summary>
    private static JsonObject ValidateReport(JsonObject report)
    {
        var keys = report.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (string.Join(",", keys) != "blocker,evidence,nextSteps,status,summary")
        {
            throw new InvalidOperationException("Ralph workflow returned a malformed round report");
        }
        var status = StringProperty(report, "status");
        var summary = StringProperty(report, "summary");
        var evidence = StringList(report, "evidence");
        var nextSteps = StringList(report, "nextSteps");
        var blocker = StringProperty(report, "blocker");
        if (status is not ("continue" or "complete" or "blocked"))
        {
            throw new InvalidOperationException("Ralph round report status is invalid");
        }
        if (!NormalizedText(summary)) throw new InvalidOperationException("Ralph round report summary must be non-empty and normalized");
        if (!NormalizedList(evidence) || !NormalizedList(nextSteps))
        {
            throw new InvalidOperationException("Ralph round report evidence and nextSteps must contain only non-empty normalized strings");
        }
        if (!NormalizedText(blocker, allowEmpty: true))
        {
            throw new InvalidOperationException("Ralph round report blocker must be a normalized string");
        }
        switch (status)
        {
            case "continue":
                if (nextSteps.Length == 0 || blocker.Length > 0)
                {
                    throw new InvalidOperationException("a continuing Ralph report needs nextSteps and an empty blocker");
                }
                break;
            case "complete":
                if (evidence.Length == 0 || nextSteps.Length != 0 || blocker.Length > 0)
                {
                    throw new InvalidOperationException("a complete Ralph report needs evidence, no nextSteps, and an empty blocker");
                }
                break;
            case "blocked":
                if (blocker.Length == 0)
                {
                    throw new InvalidOperationException("a blocked Ralph report needs a concrete blocker");
                }
                break;
        }
        var serialized = JsonSerializer.Serialize(report);
        if (serialized.Length > MaxHandoffChars)
        {
            throw new InvalidOperationException($"Ralph round report exceeds maxHandoffChars ({serialized.Length} > {MaxHandoffChars})");
        }
        return report;
    }

    /// <summary>Render the fixed terminal envelope for one run outcome (port of renderResult).</summary>
    private static string RenderResult(string status, int roundsStarted, JsonObject report)
    {
        var rounds = $"{roundsStarted} round{(roundsStarted == 1 ? "" : "s")}";
        var pretty = PrettyJson(report);
        var text = status switch
        {
            "complete" => $"Ralph worker reported completion after {rounds}.\nFinal report:\n{pretty}",
            "blocked" => $"Ralph worker reported a blocker after {rounds}.\nFinal report:\n{pretty}",
            _ => $"Ralph reached its {rounds} limit; the worker reported work remaining.\nFinal report:\n{pretty}",
        };
        return text.Length <= MaxHandoffChars ? text : text[..MaxHandoffChars] + "\n… [truncated]";
    }

    /// <summary>Two-space-indented JSON with LF line endings (the recorded report spelling).</summary>
    private static string PrettyJson(JsonObject report)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            report.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static int ResolveMaxRounds(int? requested)
    {
        var value = requested ?? DefaultMaxRounds;
        if (value < 1) throw new ArgumentException("Ralph maxRounds must be a positive safe integer");
        if (value > DefaultMaxRounds) throw new ArgumentException($"Ralph maxRounds {value} exceeds the deployment ceiling {DefaultMaxRounds}");
        return value;
    }

    private static bool NormalizedText(string? value, bool allowEmpty = false)
        => value is not null && value == value.Trim() && (allowEmpty || value.Length > 0);

    private static bool NormalizedList(IReadOnlyList<string> values) => values.All(value => NormalizedText(value));

    private static string StringProperty(JsonObject report, string key)
        => report[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string[] StringList(JsonObject report, string key)
        => report[key] is JsonArray array
            ? array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty).ToArray()
            : Array.Empty<string>();
}