using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Interaction;

/// <summary>
/// Model-facing Consumer of the user-questions seam: the <c>ask_user_question</c> tool pauses
/// until a composed answerer returns a human answer, then feeds that answer back into the agent
/// loop as an ordinary tool result (port of the TS <c>tool-ask-user</c>).
/// </summary>
public static class AskUserTool
{
    /// <summary>The model-facing description (pinned literal).</summary>
    public const string Description =
        "Ask the user a concise question when you need confirmation, a choice, or missing information before proceeding. "
        + "Send one or more questions, each with a stable id that will be echoed in the answer.";

    /// <summary>The model-facing parameters schema (pinned literal).</summary>
    public const string ParametersSchemaJson =
        "{\"questions\":{\"type\":\"array\",\"required\":true,\"description\":\"Questions to ask the user before continuing.\",\"items\":{\"type\":\"object\",\"additionalProperties\":true,\"properties\":{\"id\":{\"type\":\"string\",\"required\":true,\"description\":\"Stable id for this question; echoed in the answer.\"},\"question\":{\"type\":\"string\",\"required\":true,\"description\":\"The specific question to ask the user.\"},\"header\":{\"type\":\"string\",\"description\":\"Optional short heading for the question, such as \\\"Confirm\\\" or \\\"Choose Mode\\\".\"},\"options\":{\"type\":\"array\",\"description\":\"Optional choices to show the user. If you recommend one, put it first and append \\\"(Recommended)\\\" to that label.\",\"items\":{\"type\":\"object\",\"additionalProperties\":true,\"properties\":{\"label\":{\"type\":\"string\",\"required\":true,\"description\":\"Short user-facing option label.\"},\"description\":{\"type\":\"string\",\"description\":\"One sentence explaining the tradeoff or impact.\"}}}},\"multi_select\":{\"type\":\"boolean\",\"description\":\"Whether the user may select more than one option. Defaults to false.\"}}}}}";

    /// <summary>The canonical output schema (pinned literal).</summary>
    public const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"answers\":{\"type\":\"array\",\"required\":true,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"id\":{\"type\":\"string\",\"required\":true},\"selected\":{\"type\":\"array\",\"required\":true,\"items\":{\"type\":\"string\"}},\"custom\":{\"type\":\"string\"}}}}}}";

    /// <summary>
    /// Build the ask_user_question ToolDefinition over the mounted user-questions service. Execute
    /// parses the model arguments, asks the answerer waterfall, and returns the human answers.
    /// </summary>
    public static ToolDefinition Definition(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var questions = ctx.Get<UserQuestionService>("userQuestions")
            ?? throw new InvalidOperationException("ask_user_question: the \"userQuestions\" service is not mounted");
        return new ToolDefinition(
            Name: "ask_user_question",
            Description: Description,
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: async (args, context) =>
            {
                // A delegated caller cannot pause its parent for human input: the recorded corpus
                // refuses with the DELEGATED_CALLER guidance (the TS delegated-caller gate).
                if (context.Session is { Header.DelegationDepth: > 0 })
                {
                    throw new UserQuestionError(
                        "human interaction is unavailable while the calling agent is owned by another live agent; include the unresolved question or decision in the child agent's final result",
                        "DELEGATED_CALLER");
                }
                var answer = await questions.AskAsync(new UserQuestionRequest(
                    ParseQuestions(args),
                    CancellationToken: context.CancellationToken));
                return JsonSerializer.SerializeToElement(answer);
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(value.ToString()) });
    }

    private static IReadOnlyList<UserQuestionItem> ParseQuestions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
        {
            throw new UserQuestionError("ask_user_question requires a \"questions\" array", "EMPTY_QUESTIONS");
        }
        var items = new List<UserQuestionItem>();
        foreach (var question in questions.EnumerateArray())
        {
            if (!question.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !question.TryGetProperty("question", out var text) || text.ValueKind != JsonValueKind.String)
            {
                throw new UserQuestionError("every question needs a string id and question text", "EMPTY_QUESTIONS");
            }
            IReadOnlyList<UserQuestionOption>? options = null;
            if (question.TryGetProperty("options", out var optionList) && optionList.ValueKind == JsonValueKind.Array)
            {
                var parsed = new List<UserQuestionOption>();
                foreach (var option in optionList.EnumerateArray())
                {
                    if (!option.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String)
                    {
                        throw new UserQuestionError("every option needs a string label", "EMPTY_QUESTIONS");
                    }
                    parsed.Add(new UserQuestionOption(
                        label.GetString()!,
                        option.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                            ? description.GetString()
                            : null));
                }
                options = parsed;
            }
            items.Add(new UserQuestionItem(
                id.GetString()!,
                text.GetString()!,
                Header: question.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.String ? header.GetString() : null,
                Options: options,
                MultiSelect: question.TryGetProperty("multi_select", out var multi) && multi.ValueKind == JsonValueKind.True));
        }
        if (items.Count == 0)
        {
            throw new UserQuestionError("ask_user_question requires at least one question", "EMPTY_QUESTIONS");
        }
        return items;
    }
}
