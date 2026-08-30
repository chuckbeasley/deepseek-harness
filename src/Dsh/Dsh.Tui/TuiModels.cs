using System.Text.Json;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Spike;

namespace Dsh.Tui;

/// <summary>The role of one transcript row.</summary>
public enum TranscriptRowKind
{
    User,
    Assistant,
    Tool,
}

/// <summary>One immutable transcript row projected from the session log.</summary>
public sealed record TranscriptRow(long Seq, TranscriptRowKind Kind, string Text, string? Detail = null, bool IsError = false);

/// <summary>
/// Live transcript projection: user/assistant messages as rows, tool calls as rows that a later
/// tool/result completes. Pure model — no Terminal.Gui types — so it is testable headlessly.
/// </summary>
public sealed class TranscriptModel
{
    private readonly List<TranscriptRow> _rows = new();
    private readonly Dictionary<ToolCallId, int> _toolRows = new();

    /// <summary>Raised after any mutation.</summary>
    public event Action? Changed;

    /// <summary>The projected rows, oldest first.</summary>
    public IReadOnlyList<TranscriptRow> Rows => _rows;

    /// <summary>Rebuild the projection from a full log (the resume path).</summary>
    public void Reset(IReadOnlyList<SessionEvent> events)
    {
        _rows.Clear();
        _toolRows.Clear();
        foreach (var evt in events) Apply(evt);
    }

    /// <summary>Fold one appended session event into the projection.</summary>
    public void Apply(SessionEvent evt)
    {
        switch (evt)
        {
            case UserMessageEvent user:
                _rows.Add(new TranscriptRow(user.Seq, TranscriptRowKind.User, RenderBlocks(user.Message.Content), Detail: DescribeSource(user.Message.Source)));
                break;
            case AssistantMessageEvent assistant when !assistant.Interrupted:
                _rows.Add(new TranscriptRow(assistant.Seq, TranscriptRowKind.Assistant, RenderBlocks(assistant.Message.Content), Detail: assistant.Usage is { } usage ? $"usage: in {usage.InputTokens}, out {usage.OutputTokens}" : null));
                break;
            case AssistantMessageEvent assistant:
                _rows.Add(new TranscriptRow(assistant.Seq, TranscriptRowKind.Assistant, RenderBlocks(assistant.Message.Content), Detail: "interrupted"));
                break;
            case ToolCallEvent call:
                _toolRows[call.CallId] = _rows.Count;
                _rows.Add(new TranscriptRow(call.Seq, TranscriptRowKind.Tool, $"{call.Name}({Summarize(call.Arguments)})", Detail: call.Arguments));
                break;
            case ToolResultEvent result:
                var rowIndex = _toolRows.GetValueOrDefault(ResultCallId(result));
                if (rowIndex >= 0 && rowIndex < _rows.Count)
                {
                    var row = _rows[rowIndex];
                    _rows[rowIndex] = row with
                    {
                        Text = $"{row.Text} -> {RenderBlocks(result.Message.Content)}",
                        IsError = result.Error is not null,
                        Detail = result.Error is { } error ? $"{error.Name}: {error.Code}" : row.Detail,
                    };
                }
                break;
        }
        Changed?.Invoke();
    }

    private static ToolCallId ResultCallId(ToolResultEvent result)
        => result.Message.Source is ToolSource { CallId: var callId } ? callId : new ToolCallId(string.Empty);

    private static string DescribeSource(MessageSource source) => source switch
    {
        UserSource => "user",
        ModelSource model => $"{model.Provider}/{model.Model}",
        PluginSource plugin => $"plugin {plugin.Plugin}",
        ToolSource tool => $"tool {tool.CallId}",
        _ => source.Kind,
    };

    private static string RenderBlocks(IReadOnlyList<ContentBlock> blocks)
    {
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    parts.Add(text.Text);
                    break;
                case ReasoningBlock reasoning:
                    parts.Add($"[reasoning] {reasoning.Text}");
                    break;
                case ToolCallBlock call:
                    parts.Add($"[tool] {call.Name}({Summarize(call.Arguments)})");
                    break;
                case ToolResultBlock toolResult:
                    parts.Add($"[tool result] {RenderBlocks(toolResult.Content)}");
                    break;
            }
        }
        return string.Join('\n', parts.Where(part => part.Length > 0));
    }

    private static string Summarize(string json)
    {
        if (json.Length == 0) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

/// <summary>One row of the todo panel (last todo/write event wins).</summary>
public sealed record TodoPanelRow(string Content, string Status);

/// <summary>Pure todo-panel projection from the session's <c>todo/write</c> events.</summary>
public sealed class TodoModel
{
    private IReadOnlyList<TodoPanelRow> _rows = Array.Empty<TodoPanelRow>();

    /// <summary>Raised after any mutation.</summary>
    public event Action? Changed;

    /// <summary>The projected todo list (empty before the first todo/write).</summary>
    public IReadOnlyList<TodoPanelRow> Rows => _rows;

    /// <summary>Rebuild from a full log.</summary>
    public void Reset(IReadOnlyList<SessionEvent> events)
    {
        _rows = Array.Empty<TodoPanelRow>();
        foreach (var evt in events)
        {
            if (evt is TodoWriteEvent todo) Apply(todo);
        }
    }

    /// <summary>Fold one todo/write event; the whole list replaces the previous one.</summary>
    public void Apply(TodoWriteEvent evt)
    {
        _rows = evt.Todos.Select(item => new TodoPanelRow(item.Content, StatusLabel(item.Status))).ToArray();
        Changed?.Invoke();
    }

    private static string StatusLabel(TodoItemStatus status) => status switch
    {
        TodoItemStatus.Pending => "pending",
        TodoItemStatus.InProgress => "in progress",
        TodoItemStatus.Completed => "completed",
        _ => status.ToString(),
    };
}

/// <summary>The outcome the operator chose for one pending tool approval.</summary>
public enum ApprovalOutcome
{
    Approved,
    Denied,
}

/// <summary>One pending tool approval awaiting the operator (the TUI owns the dialog).</summary>
public sealed class PendingApproval
{
    private readonly TaskCompletionSource<ApprovalOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingApproval(string toolName, JsonElement arguments)
    {
        ToolName = toolName;
        Arguments = arguments;
    }

    /// <summary>The tool's registered name.</summary>
    public string ToolName { get; }

    /// <summary>The parsed model arguments.</summary>
    public JsonElement Arguments { get; }

    /// <summary>The outcome promise the pre-execute listener awaits.</summary>
    public Task<ApprovalOutcome> Outcome => _outcome.Task;

    /// <summary>Resolve the operator's decision (idempotent).</summary>
    public void Decide(ApprovalOutcome outcome) => _outcome.TrySetResult(outcome);
}
