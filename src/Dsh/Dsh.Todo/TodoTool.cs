using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Todo;

/// <summary>
/// Model-facing Consumer of the todo capability: the <c>todo_write</c> tool over
/// <see cref="ITodoService"/>. Each call replaces the previous list (last-write-wins) and appends
/// the durable <see cref="TodoWriteEvent"/> through the owning session.
/// </summary>
public static class TodoTool
{
    /// <summary>Model-facing description for one activation (pinned literal).</summary>
    public static string Describe(bool allowParallelInProgress)
    {
        const string head = "Record and update a structured task list for the current work. Send the ENTIRE "
            + "list every call — it REPLACES the previous list (there are no partial updates, "
            + "no per-item edits). Use it to plan multi-step work and show progress: add one "
            + "todo per concrete step before you start. ";
        const string parallel = "Mark every todo being actively worked "
            + "on `in_progress` — several at once when work genuinely runs in parallel (e.g. "
            + "concurrent subagents or background commands), one for sequential work; while "
            + "work remains, at least one task should be `in_progress`. ";
        const string single = "Keep AT MOST ONE todo "
            + "`in_progress` at a time; while work remains, exactly one active task should be "
            + "`in_progress`. ";
        const string tail = "Mark a todo "
            + "`completed` the moment it is done (do not batch completions), and allow no "
            + "`in_progress` item only once all work is complete. Skip the list for trivial "
            + "single-step tasks. Statuses: `pending` (not started), `in_progress` (being "
            + "worked on now), `completed` (finished).";
        return head + (allowParallelInProgress ? parallel : single) + tail;
    }

    /// <summary>The model-facing parameters schema (pinned literal).</summary>
    public const string ParametersSchemaJson =
        "{\"todos\":{\"type\":\"array\",\"required\":true,\"description\":\"The COMPLETE task list, replacing any previous list.\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"content\":{\"type\":\"string\",\"required\":true,\"description\":\"What the task is — a short imperative line.\"},\"status\":{\"type\":\"string\",\"required\":true,\"enum\":[\"pending\",\"in_progress\",\"completed\"],\"description\":\"pending (not started) | in_progress (now) | completed (done).\"}}}}}";

    /// <summary>The canonical output schema (pinned literal).</summary>
    public const string OutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"todos\":{\"type\":\"array\",\"required\":true,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"content\":{\"type\":\"string\",\"required\":true},\"status\":{\"type\":\"string\",\"required\":true,\"enum\":[\"pending\",\"in_progress\",\"completed\"]}}}},\"counts\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{\"pending\":{\"type\":\"integer\",\"required\":true},\"inProgress\":{\"type\":\"integer\",\"required\":true},\"completed\":{\"type\":\"integer\",\"required\":true}}}}}";

    /// <summary>
    /// Build the todo_write ToolDefinition over the mounted todo service. Execute parses the model
    /// arguments, replaces the list, and appends the durable todo/write event; Render projects the
    /// canonical value to the model-facing text block.
    /// </summary>
    public static ToolDefinition Definition(Context ctx, bool allowParallelInProgress = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var service = ctx.Get<ITodoService>("todo")
            ?? throw new InvalidOperationException("todo_write: the \"todo\" service is not mounted");
        SessionEventTypes.Register(TodoWriteEvent.EventTypeName, typeof(TodoWriteEvent));
        return new ToolDefinition(
            Name: "todo_write",
            Description: Describe(allowParallelInProgress),
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, context) =>
            {
                var result = service.Replace(ParseTodos(args));
                context.Session?.Append(new TodoWriteEvent { Todos = result.Todos });
                return Task.FromResult(JsonSerializer.SerializeToElement(result));
            },
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderText(value)) });
    }

    private static IReadOnlyList<TodoItem> ParseTodos(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("todo_write arguments must carry a \"todos\" array");
        }
        var list = new List<TodoItem>();
        foreach (var item in todos.EnumerateArray())
        {
            var content = item.GetProperty("content").GetString() ?? string.Empty;
            var status = item.GetProperty("status").GetString() ?? string.Empty;
            list.Add(new TodoItem(content, status switch
            {
                "pending" => TodoItemStatus.Pending,
                "in_progress" => TodoItemStatus.InProgress,
                "completed" => TodoItemStatus.Completed,
                _ => throw new ArgumentException($"invalid todo status \"{status}\""),
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
        return $"Updated todo list: {pending} pending, {inProgress} in progress, {completed} completed.";
    }
}
