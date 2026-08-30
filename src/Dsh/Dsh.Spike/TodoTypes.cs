using System.Text.Json.Serialization;
using Dsh.Session;

namespace Dsh.Spike;

/// <summary>Lifecycle state of one todo item. Serialized with the TS wire strings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoItemStatus
{
    /// <summary>Not started.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>Being worked on now.</summary>
    [JsonStringEnumMemberName("in_progress")] InProgress,
    /// <summary>Finished.</summary>
    [JsonStringEnumMemberName("completed")] Completed,
}

/// <summary>One entry in an agent's todo list — the unit of the todo/write whole-list snapshot.</summary>
public sealed record TodoItem([property: JsonPropertyName("content")] string Content, [property: JsonPropertyName("status")] TodoItemStatus Status);

/// <summary>Canonical counts of one todo list.</summary>
public sealed record TodoCounts([property: JsonPropertyName("pending")] int Pending, [property: JsonPropertyName("inProgress")] int InProgress, [property: JsonPropertyName("completed")] int Completed);

/// <summary>The canonical result of one todo_write call: the complete list plus its counts.</summary>
public sealed record TodoWriteResult([property: JsonPropertyName("todos")] IReadOnlyList<TodoItem> Todos, [property: JsonPropertyName("counts")] TodoCounts Counts);

/// <summary>
/// Plugin-merged session event: whole-list todo snapshot (last write wins on replay; log-only UI
/// state, never derived history). Registered into the session event-type registry at boot (part 2).
/// </summary>
public sealed record TodoWriteEvent : SessionEvent
{
    /// <summary>The complete replacement list.</summary>
    public required IReadOnlyList<TodoItem> Todos { get; init; }

    public override string Type => "todo/write";
}

