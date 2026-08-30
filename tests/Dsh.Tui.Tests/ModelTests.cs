using System.Text.Json;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Todo;
using Dsh.Tui;

namespace Dsh.Tui.Tests;

/// <summary>Headless tests of the pure view models.</summary>
public static class ModelTests
{
    public static async Task Transcript_FoldsMessagesAndCompletesToolRows()
    {
        var session = NewSession();
        var model = new TranscriptModel();

        var user = new UserMessage
        {
            Id = new MessageId("u1"),
            Content = new ContentBlock[] { new TextBlock("hello") },
            Source = new UserSource(),
        };
        session.Append(new UserMessageEvent { Message = user, SurfaceOp = SurfaceOp.Append });
        model.Apply(session.Events[^1]);
        Assert.Equal(1, model.Rows.Count, "one user row");
        Assert.Equal(TranscriptRowKind.User, model.Rows[0].Kind, "the row is a user row");
        Assert.Equal("hello", model.Rows[0].Text, "the row carries the text");

        var assistant = new AssistantMessage
        {
            Id = new MessageId("a1"),
            Content = new ContentBlock[] { new ToolCallBlock(new ToolCallId("call-1"), "todo_write", "{\"todos\":[]}") },
            Source = new ModelSource { Provider = "mock", Model = "mock-todo" },
        };
        session.Append(new AssistantMessageEvent { Turn = 1, Step = 1, Message = assistant, SurfaceOp = SurfaceOp.Append });
        model.Apply(session.Events[^1]);
        Assert.Equal(2, model.Rows.Count, "the assistant message adds a row");
        Assert.True(model.Rows[1].Text.Contains("[tool] todo_write"), "tool-call blocks render inline in the assistant row");

        session.Append(new ToolCallEvent
        {
            Turn = 1, Step = 1, CallId = new ToolCallId("call-1"), Name = "todo_write",
            Arguments = "{\"todos\":[]}",
        });
        model.Apply(session.Events[^1]);
        Assert.Equal(3, model.Rows.Count, "the tool call adds a row");
        Assert.Equal(TranscriptRowKind.Tool, model.Rows[2].Kind, "the third row is a tool row");

        var result = ToolResultMessage.Create(new ToolCallId("call-1"), new ContentBlock[] { new TextBlock("done") }, isError: false);
        session.Append(new ToolResultEvent
        {
            Turn = 1, Step = 1, Message = result, SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { 2 },
        });
        model.Apply(session.Events[^1]);
        Assert.Equal(3, model.Rows.Count, "the tool result updates the tool row, it does not add one");
        Assert.True(model.Rows[2].Text.Contains("done"), "the tool row shows the rendered result");
        Assert.False(model.Rows[2].IsError, "a successful result is not an error");

        var failed = ToolResultMessage.Create(new ToolCallId("call-1"), new ContentBlock[] { new TextBlock("boom") }, isError: true);
        session.Append(new ToolResultEvent
        {
            Turn = 1, Step = 1, Message = failed, SurfaceOp = SurfaceOp.Append,
            Error = new ToolErrorInfo("Error", "UNKNOWN"),
            SourceEventSeqs = new long[] { 2 },
        });
        model.Apply(session.Events[^1]);
        Assert.True(model.Rows[2].IsError, "a failed result marks the tool row as an error");

        var interrupted = new AssistantMessage
        {
            Id = new MessageId("a2"),
            Content = new ContentBlock[] { new TextBlock("partial") },
            Source = new ModelSource { Provider = "mock", Model = "mock-todo" },
        };
        session.Append(new AssistantMessageEvent
        {
            Turn = 1, Step = 1,
            Message = interrupted, SurfaceOp = SurfaceOp.Append, Interrupted = true,
        });
        model.Apply(session.Events[^1]);
        Assert.Equal("interrupted", model.Rows[^1].Detail, "an interrupted assistant row carries the interrupted detail");
    }

    public static async Task Todo_LastWriteWins_AndResetReplays()
    {
        var session = NewSession();
        var model = new TodoModel();
        session.Append(new TodoWriteEvent { Todos = new[] { new TodoItem("first", TodoItemStatus.Pending) } });
        model.Apply((TodoWriteEvent)session.Events[^1]);
        Assert.Equal(1, model.Rows.Count, "one todo row after the first write");

        session.Append(new TodoWriteEvent
        {
            Todos = new[]
            {
                new TodoItem("first", TodoItemStatus.Completed),
                new TodoItem("second", TodoItemStatus.InProgress),
            },
        });
        model.Apply((TodoWriteEvent)session.Events[^1]);
        Assert.Equal(2, model.Rows.Count, "the second write replaces the whole list");
        Assert.Equal("completed", model.Rows[0].Status, "the first item's new status applies");

        var replayed = new TodoModel();
        replayed.Reset(session.Events);
        Assert.Equal(2, replayed.Rows.Count, "reset folds the full log to the same last-write state");
    }

    public static async Task PendingApproval_DecidesOnce()
    {
        var pending = new PendingApproval("todo_write", JsonDocument.Parse("{\"todos\":[]}").RootElement.Clone());
        Assert.False(pending.Outcome.IsCompleted, "the outcome is pending until decided");
        pending.Decide(ApprovalOutcome.Approved);
        Assert.Equal(ApprovalOutcome.Approved, await pending.Outcome, "the decision resolves the outcome");
        pending.Decide(ApprovalOutcome.Denied);
        Assert.Equal(ApprovalOutcome.Approved, await pending.Outcome, "a second decision is ignored");
    }

    public static async Task TuiApp_SmokeRunsHeadlessly()
    {
        Assert.Equal(0, TuiApp.Run(new[] { "--smoke" }), "the fake-driver smoke must exit 0");
    }

    private static Dsh.Session.Session NewSession()
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        return sessions.Create(new SessionId("session-tui-test"));
    }
}
