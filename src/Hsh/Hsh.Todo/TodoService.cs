using Harness.Cordis.Core;

namespace Harness.Todo;

/// <summary>
/// In-memory todo provider (ctx.todo): the whole-list state the todo_write consumer replaces on
/// every call. Deployment policy (approval, persistence layout) belongs to other seams.
/// </summary>
public sealed class TodoService : Service, ITodoService
{
    private readonly bool _allowParallelInProgress;
    private readonly List<TodoItem> _todos = new();

    /// <summary>Create the provider and register it as <c>todo</c>.</summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <param name="allowParallelInProgress">whether more than one in_progress item is valid.</param>
    public TodoService(Context ctx, bool allowParallelInProgress = false)
        : base(ctx, "todo")
    {
        _allowParallelInProgress = allowParallelInProgress;
    }

    /// <inheritdoc />
    public IReadOnlyList<TodoItem> Get() => _todos.ToArray();

    /// <inheritdoc />
    public TodoWriteResult Replace(IReadOnlyList<TodoItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
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
        todos.Count(item => item.Status == TodoItemStatus.Pending),
        todos.Count(item => item.Status == TodoItemStatus.InProgress),
        todos.Count(item => item.Status == TodoItemStatus.Completed));
}
