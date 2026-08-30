using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Spike;
using Dsh.Tools;

namespace Dsh.Spike.Tests;

public static class TodoToolTests
{
    private static readonly TodoItem[] Plan =
    {
        new("Port the session event log", TodoItemStatus.InProgress),
        new("Port the mock LLM adapter", TodoItemStatus.Pending),
        new("Port the todo tool", TodoItemStatus.Pending),
    };

    public static void Write_ReplacesWholeList_AndComputesCounts()
    {
        var tool = new TodoTool(allowParallelInProgress: false);
        tool.Write(new[] { new TodoItem("a", TodoItemStatus.Pending) });
        var second = tool.Write(Plan);

        Assert.Equal(3, tool.Todos.Count);
        Assert.Equal(2, second.Counts.Pending);
        Assert.Equal(1, second.Counts.InProgress);
        Assert.Equal(0, second.Counts.Completed);
        Assert.Equal(Plan, tool.Todos);
    }

    public static void Write_RejectsEmptyContent()
    {
        Assert.Throws<ArgumentException>(() =>
            new TodoTool(false).Write(new[] { new TodoItem("   ", TodoItemStatus.Pending) }));
    }

    public static void Write_RejectsDuplicateContent()
    {
        Assert.Throws<ArgumentException>(() =>
            new TodoTool(false).Write(new[]
            {
                new TodoItem("x", TodoItemStatus.Pending),
                new TodoItem("x", TodoItemStatus.Pending),
            }));
    }

    public static void Write_RejectsTwoInProgress_WhenParallelDisabled()
    {
        Assert.Throws<ArgumentException>(() =>
            new TodoTool(false).Write(new[]
            {
                new TodoItem("a", TodoItemStatus.InProgress),
                new TodoItem("b", TodoItemStatus.InProgress),
            }));
    }

    public static void Write_AllowsTwoInProgress_WhenParallelEnabled()
    {
        var tool = new TodoTool(allowParallelInProgress: true);
        var result = tool.Write(new[]
        {
            new TodoItem("a", TodoItemStatus.InProgress),
            new TodoItem("b", TodoItemStatus.InProgress),
        });
        Assert.Equal(2, result.Counts.InProgress);
    }

    public static void Describe_MatchesThePinnedFixtureLiteral()
    {
        const string expected =
            "Record and update a structured task list for the current work. Send the ENTIRE "
            + "list every call — it REPLACES the previous list (there are no partial updates, "
            + "no per-item edits). Use it to plan multi-step work and show progress: add one "
            + "todo per concrete step before you start. Keep AT MOST ONE todo "
            + "`in_progress` at a time; while work remains, exactly one active task should be "
            + "`in_progress`. Mark a todo "
            + "`completed` the moment it is done (do not batch completions), and allow no "
            + "`in_progress` item only once all work is complete. Skip the list for trivial "
            + "single-step tasks. Statuses: `pending` (not started), `in_progress` (being "
            + "worked on now), `completed` (finished).";
        Assert.Equal(expected, TodoTool.Describe(false));
    }

    public static void Definition_Execute_ReturnsCanonicalResult_AndRender_ProjectsText()
    {
        var definition = TodoTool.Definition(allowParallelInProgress: false);
        Assert.Equal("todo_write", definition.Name);
        Assert.NotNull(definition.Render);

        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"todos\":[{\"content\":\"Port the session log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock\",\"status\":\"pending\"}]}")
            !);
        var context = new ToolRunContext(new ToolCallId("call-1"), "todo_write", args, CancellationToken.None);
        var resultJson = definition.Execute(args, context).GetAwaiter().GetResult();

        var counts = resultJson.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("pending").GetInt32());
        Assert.Equal(1, counts.GetProperty("inProgress").GetInt32());
        Assert.Equal(0, counts.GetProperty("completed").GetInt32());

        var block = Assert.IsType<TextBlock>(definition.Render!(args, resultJson)[0]);
        Assert.Equal("Updated todo list: 1 pending, 1 in progress, 0 completed.", block.Text);
    }

    public static void TodoWriteEvent_RoundTripsAsDeclaredType()
    {
        var evt = new TodoWriteEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            Todos = new[] { new TodoItem("Port the session log", TodoItemStatus.InProgress) },
        };

        var json = JsonSerializer.Serialize(evt);
        var back = JsonSerializer.Deserialize<TodoWriteEvent>(json);
        Assert.NotNull(back);
        Assert.Equal(evt, back);
        Assert.Equal("todo/write", back!.Type);
    }
}


