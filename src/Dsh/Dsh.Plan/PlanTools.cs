using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Plan;

/// <summary>
/// The model-facing whole-plan replacement tool: plan_write. Each call replaces the previous plan
/// (last-write-wins) and appends the durable <see cref="PlanWriteEvent"/> snapshot to the calling
/// session; validation mirrors the todo tool (trimmed non-empty unique content, at most one item
/// in_progress). The plan event type's registry registration happens at the plan service's
/// construction (composition), so the JSONL backend can round-trip it.
/// </summary>
public static class PlanTools
{
    /// <summary>Model-facing description for one activation (pinned literal).</summary>
    public static string Describe()
    {
        const string text =
            "Record and update the current plan. Send the ENTIRE plan every call — it REPLACES "
            + "the previous plan (there are no partial updates, no per-item edits). Use it to lay "
            + "out the work ahead before you start: add one plan item per concrete step. Keep AT "
            + "MOST ONE item `in_progress` at a time; while work remains, exactly one active item "
            + "should be `in_progress`. Mark an item `completed` the moment it is done (do not "
            + "batch completions), and allow no `in_progress` item only once the whole plan is "
            + "complete. Statuses: `pending` (not started), `in_progress` (being worked on now), "
            + "`completed` (finished).";
        return text;
    }

    /// <summary>The model-facing parameters schema (pinned literal).</summary>
    public const string ParametersSchemaJson =
        "{\"plan\":{\"type\":\"array\",\"required\":true,\"description\":\"The COMPLETE plan, replacing any previous plan.\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"content\":{\"type\":\"string\",\"required\":true,\"description\":\"What this plan item is — a short imperative line.\"},\"status\":{\"type\":\"string\",\"required\":true,\"enum\":[\"pending\",\"in_progress\",\"completed\"],\"description\":\"pending (not started) | in_progress (now) | completed (done).\"}}}}}";

    /// <summary>The canonical output schema (pinned literal).</summary>
    public const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"plan\":{\"type\":\"array\",\"required\":true,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"content\":{\"type\":\"string\",\"required\":true},\"status\":{\"type\":\"string\",\"required\":true,\"enum\":[\"pending\",\"in_progress\",\"completed\"]}}}},\"counts\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{\"pending\":{\"type\":\"integer\",\"required\":true},\"inProgress\":{\"type\":\"integer\",\"required\":true},\"completed\":{\"type\":\"integer\",\"required\":true}}}}}";

    /// <summary>
    /// Build the plan_write ToolDefinition. Execute parses the model arguments, validates, appends
    /// the durable plan/write event to the calling session, and returns the canonical {plan,
    /// counts} value; Render projects the canonical value to the model-facing text block.
    /// </summary>
    public static ToolDefinition Definition()
    {
        return new ToolDefinition(
            Name: "plan_write",
            Description: Describe(),
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var result = Write(ParsePlan(args));
                context.Session?.Append(new PlanWriteEvent { Plan = result.Plan });
                return Task.FromResult(JsonSerializer.SerializeToElement(result));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderText(value)) });
    }

    /// <summary>Validate and replace the whole plan; returns the canonical result.</summary>
    /// <exception cref="ArgumentException">An item is empty after trimming, content duplicates, or more than one item is in_progress.</exception>
    public static PlanWriteResult Write(IReadOnlyList<PlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var plan = Validate(items);
        return new PlanWriteResult(plan, Counts(plan));
    }

    private static IReadOnlyList<PlanItem> Validate(IReadOnlyList<PlanItem> raw)
    {
        var plan = new List<PlanItem>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var active = 0;
        foreach (var item in raw)
        {
            var content = item.Content.Trim();
            if (content.Length == 0)
            {
                throw new ArgumentException("invalid plan: `content` must be a non-empty string");
            }
            if (!seen.Add(content))
            {
                throw new ArgumentException($"invalid plan: duplicate content \"{content}\"");
            }
            if (item.Status == PlanItemStatus.InProgress) active++;
            plan.Add(item with { Content = content });
        }
        if (active > 1)
        {
            throw new ArgumentException($"invalid plan: at most one item may be in_progress (got {active})");
        }
        return plan;
    }

    private static PlanCounts Counts(IReadOnlyList<PlanItem> plan) => new(
        plan.Count(item => item.Status == PlanItemStatus.Pending),
        plan.Count(item => item.Status == PlanItemStatus.InProgress),
        plan.Count(item => item.Status == PlanItemStatus.Completed));

    private static IReadOnlyList<PlanItem> ParsePlan(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("plan", out var plan)
            || plan.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("plan_write arguments must carry a \"plan\" array");
        }
        var list = new List<PlanItem>();
        foreach (var item in plan.EnumerateArray())
        {
            var content = item.GetProperty("content").GetString() ?? string.Empty;
            var status = item.GetProperty("status").GetString() ?? string.Empty;
            list.Add(new PlanItem(content, status switch
            {
                "pending" => PlanItemStatus.Pending,
                "in_progress" => PlanItemStatus.InProgress,
                "completed" => PlanItemStatus.Completed,
                _ => throw new ArgumentException($"invalid plan item status \"{status}\""),
            }));
        }
        return list;
    }

    private static string RenderText(JsonElement value)
    {
        var counts = value.GetProperty("counts");
        var pending = counts.GetProperty("pending").GetInt32();
        var inProgress = counts.GetProperty("inProgress").GetInt32();
        var completed = counts.GetProperty("completed").GetInt32();
        return $"Updated plan: {pending} pending, {inProgress} in progress, {completed} completed.";
    }
}
