namespace Dsh.Todo;

/// <summary>
/// Service Definition for the todo capability: one agent-scoped whole-list todo state with
/// last-write-wins replacement semantics. Consumers read the current snapshot or replace it;
/// durable todo/write events belong to the tool consumer.
/// </summary>
public interface ITodoService
{
    /// <summary>The current whole list, as a snapshot.</summary>
    IReadOnlyList<TodoItem> Get();

    /// <summary>
    /// Validate and replace the whole list; returns the canonical list and counts. Validation
    /// mirrors the TS tool: trimmed non-empty unique content, at most one in_progress item unless
    /// parallel work is allowed.
    /// </summary>
    TodoWriteResult Replace(IReadOnlyList<TodoItem> items);
}
