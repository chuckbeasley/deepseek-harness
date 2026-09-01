using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Session;
using Harness.Todo;
using Harness.Tools;

namespace Harness.Todo.Tests;

/// <summary>The todo service semantics, the todo_write consumer, and the durable event round-trip.</summary>
public static class TodoTests
{
    private static readonly TodoItem[] Plan =
    {
        new("Port the session event log", TodoItemStatus.InProgress),
        new("Port the mock LLM adapter", TodoItemStatus.Pending),
        new("Port the todo tool", TodoItemStatus.Pending),
    };

    public static void Service_ReplacesWholeList_AndComputesCounts()
    {
        var ctx = new Context();
        var service = new TodoService(ctx, allowParallelInProgress: false);
        var result = service.Replace(Plan);
        Assert.Equal(2, result.Counts.Pending, "two pending items");
        Assert.Equal(1, result.Counts.InProgress, "one in-progress item");
        Assert.Equal(0, result.Counts.Completed, "no completed items");
        var replaced = service.Replace(new[] { new TodoItem("Port the todo tool", TodoItemStatus.Completed) });
        Assert.Equal(1, replaced.Todos.Count, "the replacement list wins whole");
        Assert.Equal(0, replaced.Counts.Pending, "the new list's counts apply");
        ctx.Dispose();
    }

    public static void Service_RejectsInvalidLists()
    {
        var ctx = new Context();
        var service = new TodoService(ctx, allowParallelInProgress: false);
        ExpectInvalid(() => service.Replace(new[] { new TodoItem("   ", TodoItemStatus.Pending) }), "empty content");
        ExpectInvalid(() => service.Replace(new[]
        {
            new TodoItem("x", TodoItemStatus.Pending),
            new TodoItem("x", TodoItemStatus.Pending),
        }), "duplicate content");
        ExpectInvalid(() => service.Replace(new[]
        {
            new TodoItem("a", TodoItemStatus.InProgress),
            new TodoItem("b", TodoItemStatus.InProgress),
        }), "two in_progress under the single-active policy");
        ctx.Dispose();
    }

    public static void Service_AllowsParallelInProgress_WhenEnabled()
    {
        var ctx = new Context();
        var service = new TodoService(ctx, allowParallelInProgress: true);
        var result = service.Replace(new[]
        {
            new TodoItem("a", TodoItemStatus.InProgress),
            new TodoItem("b", TodoItemStatus.InProgress),
        });
        Assert.Equal(2, result.Counts.InProgress, "parallel work keeps both active items");
        ctx.Dispose();
    }

    public static async Task Tool_ExecutesThroughTheToolRuntime_AndAppendsTheDurableEvent()
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var tools = new ToolRuntime(ctx);
        var todoService = new TodoService(ctx, allowParallelInProgress: false);
        var registration = tools.Register(TodoTool.Definition(ctx, allowParallelInProgress: false));
        var session = sessions.Create(new SessionId("session-todo-test"));

        var input = new ToolExecutionInput(
            new ToolCallId("call-todo-1"),
            "todo_write",
            JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "One task", status = "pending" } } }),
            CancellationToken.None)
        {
            Session = session,
        };
        var result = await tools.ExecuteAsync(input, CancellationToken.None);
        Assert.False(result.IsError, "a valid todo_write is a success");
        Assert.Equal("Updated todo list: 1 pending, 0 in progress, 0 completed.", result.Content.OfType<TextBlock>().Single().Text, "the rendered result text");
        var evt = session.Events.OfType<TodoWriteEvent>().Single();
        Assert.Equal("todo/write", evt.Type, "the durable event carries the wire discriminator");
        Assert.Equal(1, evt.Todos.Count, "the durable event holds the replacement list");

        registration.Dispose();
        ctx.Dispose();
    }

    public static void Tool_RequiresTheMountedService()
    {
        var ctx = new Context();
        var tools = new ToolRuntime(ctx);
        try
        {
            TodoTool.Definition(ctx, allowParallelInProgress: false);
            Assert.True(false, "a definition without the mounted todo service must fail loud");
        }
        catch (InvalidOperationException error)
        {
            Assert.True(error.Message.Contains("\"todo\""), "the error names the missing service");
        }
        ctx.Dispose();
    }

    public static void Event_RoundTripsTheJsonlSerializer()
    {
        var evt = new TodoWriteEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            Todos = new[] { new TodoItem("Port the session log", TodoItemStatus.InProgress) },
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var json = JsonSerializer.Serialize<SessionEvent>(evt, options);
        var back = JsonSerializer.Deserialize<SessionEvent>(json, options);
        Assert.True(back is TodoWriteEvent, "the registered event type deserializes back to its declared type");
        Assert.Equal("Port the session log", ((TodoWriteEvent)back!).Todos[0].Content, "the payload round-trips");
    }

    private static void ExpectInvalid(Action action, string label)
    {
        try
        {
            action();
            Assert.True(false, $"the {label} case must reject");
        }
        catch (ArgumentException)
        {
            // expected
        }
    }
}
