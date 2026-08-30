using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Spike;

/// <summary>
/// Model-facing whole-list replacement: the todo_write semantics against an in-memory list.
/// Each call replaces the previous list (last-write-wins); validation mirrors the TS tool
/// (trimmed non-empty unique content, at most one in_progress unless parallel work is allowed).
/// Part 1 has no session — the durable todo/write append arrives with the driver in part 2.
/// </summary>
public sealed class TodoTool
{
    private readonly bool _allowParallelInProgress;
    private readonly List<TodoItem> _todos = new();

    public TodoTool(bool allowParallelInProgress)
    {
        _allowParallelInProgress = allowParallelInProgress;
    }

    /// <summary>Current whole list, as a snapshot.</summary>
    public IReadOnlyList<TodoItem> Todos => _todos.ToArray();

    /// <summary>Model-facing description for one activation (pinned literal, see spike-design.md 6.1).</summary>
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
    /// Build the todo_write ToolDefinition. Execute parses the model arguments, validates, replaces
    /// the in-memory list, and returns the canonical {todos, counts} value; Render projects the
    /// canonical value to the model-facing text block.
    /// </summary>
    public static ToolDefinition Definition(bool allowParallelInProgress)
    {
        var tool = new TodoTool(allowParallelInProgress);
        return new ToolDefinition(
            Name: "todo_write",
            Description: Describe(allowParallelInProgress),
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(OutputSchemaJson)!),
            Execute: (args, _) => Task.FromResult(JsonSerializer.SerializeToElement(tool.Write(ParseTodos(args)))),
            Render: (_, value) => new ContentBlock[] { new TextBlock(RenderText(value)) });
    }

    /// <summary>Validate and replace the whole list; returns the canonical result.</summary>
    /// <exception cref="ArgumentException">An item is empty after trimming, content duplicates, or more than one item is in_progress under the single-active policy.</exception>
    public TodoWriteResult Write(IReadOnlyList<TodoItem> items)
    {
        var todos = Validate(items);
        _todos.Clear();
        _todos.AddRange(todos);
        return new TodoWriteResult(todos.ToArray(), Counts(todos));
    }

    private IReadOnlyList<TodoItem> Validate(IReadOnlyList<TodoItem> raw)
    {
        var todos = new List<TodoItem>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var active = 0;
        foreach (var item in raw)
        {
            var content = item.Content.Trim();
            if (content.Length == 0)
            {
                throw new ArgumentException("invalid todo: `content` must be a non-empty string");
            }
            if (!seen.Add(content))
            {
                throw new ArgumentException($"invalid todos: duplicate content \"{content}\"");
            }
            if (item.Status == TodoItemStatus.InProgress) active++;
            todos.Add(item with { Content = content });
        }
        if (!_allowParallelInProgress && active > 1)
        {
            throw new ArgumentException($"invalid todos: at most one task may be in_progress (got {active})");
        }
        return todos;
    }

    private static TodoCounts Counts(IReadOnlyList<TodoItem> todos) => new(
        todos.Count(t => t.Status == TodoItemStatus.Pending),
        todos.Count(t => t.Status == TodoItemStatus.InProgress),
        todos.Count(t => t.Status == TodoItemStatus.Completed));

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

