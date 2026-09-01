using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Llm;
using Harness.Tools;

namespace Harness.Goal;

/// <summary>
/// Model-facing Consumer of the goal capability: the <c>goal_write</c> tool over
/// <see cref="IGoalService"/>. One tool covers create and edit: with no current goal it creates a
/// fresh revision-one active goal; with a current goal it replaces objective and/or round cap at
/// the next revision. Each call appends the durable <see cref="GoalWriteEvent"/> through the
/// owning session via the service. The TS tool trio (get_goal / create_goal / update_goal with
/// pause/resume/complete/blocked actions) collapses into this single whole-value tool, mirroring
/// the C# plan_write and todo_write consumers; update actions other than edit are deferred with
/// the goal-round driver.
/// </summary>
public static class GoalTools
{
    /// <summary>Model-facing description for one activation (pinned literal).</summary>
    public static string Describe()
    {
        const string text =
            "Create or update one persisted same-session completion goal when the current direct "
            + "human request is a long-running objective that should continue across autonomous "
            + "goal rounds. Send the ENTIRE goal state every call: the objective and, when the "
            + "round budget needs a non-default cap, max_goal_rounds. With no current goal the "
            + "tool creates one; with a current goal it edits the same goal (the previous "
            + "objective and cap are replaced when supplied). Do not use this for trivial "
            + "single-turn work. The tool may infer goal intent from a direct human request in "
            + "any language; do not create a goal unless the human asked for or clearly implied "
            + "a long-running completion objective.";
        return text;
    }

    /// <summary>The model-facing parameters schema (pinned literal).</summary>
    public const string ParametersSchemaJson =
        "{\"objective\":{\"type\":\"string\",\"required\":true,\"description\":\"The concrete completion objective inferred from the direct human request.\"},\"max_goal_rounds\":{\"type\":\"number\",\"description\":\"Optional positive safe-integer limit on automatic continuation rounds.\"}}";

    /// <summary>The canonical output schema (pinned literal).</summary>
    public const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"goal\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{\"id\":{\"type\":\"string\",\"required\":true},\"revision\":{\"type\":\"integer\",\"required\":true},\"objective\":{\"type\":\"string\",\"required\":true},\"phase\":{\"type\":\"string\",\"required\":true,\"enum\":[\"active\",\"paused\",\"blocked\",\"complete\"]},\"roundsStarted\":{\"type\":\"integer\",\"required\":true},\"maxGoalRounds\":{\"type\":\"integer\",\"required\":true},\"blockedReason\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"code\":{\"type\":\"string\",\"required\":true},\"message\":{\"type\":\"string\",\"required\":true}}}}},\"activation\":{\"type\":\"string\",\"required\":true,\"enum\":[\"armed\",\"disarmed\"]}}}";

    /// <summary>
    /// Build the goal_write ToolDefinition over the goal service. Execute parses the model
    /// arguments, creates or edits through the service (which appends the durable goal/write
    /// event), and returns the canonical {goal, activation} value; Render projects the canonical
    /// value to the model-facing text block.
    /// </summary>
    /// <param name="goals">the mounted goal service; must be the live <c>goal</c> instance.</param>
    public static ToolDefinition Definition(IGoalService goals)
    {
        ArgumentNullException.ThrowIfNull(goals);
        return new ToolDefinition(
            Name: "goal_write",
            Description: Describe(),
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var session = context.Session
                    ?? throw new ArgumentException("goal_write requires an owning session");
                var (objective, maxRounds) = Parse(args);
                var current = goals.Get(session);
                GoalView goal = current is null
                    ? goals.Create(session, objective, maxRounds)
                    : goals.Edit(session, new GoalRef(current.Id, current.Revision), objective, maxRounds);
                return Task.FromResult(JsonSerializer.SerializeToElement(GoalValue(goal)));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderText(value)) });
    }

    /// <summary>Parse and validate the model arguments: a non-empty objective and an optional round cap.</summary>
    public static (string Objective, int? MaxGoalRounds) Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("objective", out var rawObjective))
        {
            throw new ArgumentException("goal_write arguments must carry an \"objective\" string");
        }
        var objective = rawObjective.GetString() ?? string.Empty;
        if (objective.Trim().Length == 0)
        {
            throw new ArgumentException("goal_write objective must be a non-empty string");
        }
        int? maxRounds = null;
        if (args.TryGetProperty("max_goal_rounds", out var rawRounds))
        {
            var value = rawRounds.ValueKind == JsonValueKind.Number ? rawRounds.GetInt32() : 0;
            if (value > 0) maxRounds = value;
        }
        return (objective, maxRounds);
    }

    /// <summary>Build the canonical compact model result; activation is an observation, not replay state.</summary>
    private static JsonObject GoalValue(GoalView goal)
    {
        var goalObject = new JsonObject
        {
            ["id"] = goal.Id,
            ["revision"] = goal.Revision,
            ["objective"] = goal.Objective,
            ["phase"] = PhaseName(goal.Phase),
            ["roundsStarted"] = goal.RoundsStarted,
            ["maxGoalRounds"] = goal.MaxGoalRounds,
        };
        if (goal.BlockedReason is not null)
        {
            goalObject["blockedReason"] = new JsonObject
            {
                ["code"] = goal.BlockedReason.Code,
                ["message"] = goal.BlockedReason.Message,
            };
        }
        return new JsonObject
        {
            ["goal"] = goalObject,
            ["activation"] = goal.Activation == GoalActivation.Armed ? "armed" : "disarmed",
        };
    }

    private static string RenderText(JsonElement value)
    {
        var goal = value.GetProperty("goal");
        var objective = goal.GetProperty("objective").GetString() ?? string.Empty;
        var revision = goal.GetProperty("revision").GetInt32();
        var phase = goal.GetProperty("phase").GetString() ?? string.Empty;
        var rounds = goal.GetProperty("roundsStarted").GetInt32();
        var max = goal.GetProperty("maxGoalRounds").GetInt32();
        return $"Updated goal: \"{objective}\" (revision {revision}, phase {phase}, {rounds} of {max} rounds started).";
    }

    private static string PhaseName(GoalPhase phase) => phase switch
    {
        GoalPhase.Active => "active",
        GoalPhase.Paused => "paused",
        GoalPhase.Blocked => "blocked",
        GoalPhase.Complete => "complete",
        _ => phase.ToString(),
    };
}
