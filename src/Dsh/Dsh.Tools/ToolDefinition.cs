using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Tools;

/// <summary>
/// A registered tool: its model-facing schema plus the execution function. The registry
/// (<c>ToolRuntime</c>) registers, presents, and executes these; <see cref="ToolRunContext"/>
/// lives in ToolExecution.cs. <paramref name="PersistMeta"/> controls whether the execute value
/// is stored as the durable tool/result <c>meta</c> (the TS tools set it only when a UI bridge
/// needs a presentation payload — the bash tool keeps it off); <paramref name="MetaOf"/>
/// projects a dedicated meta value from the execute value when the durable meta differs from it
/// (the write tool's <c>{diffs}</c>).
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    JsonElement OutputSchema,
    Func<JsonElement, ToolRunContext, Task<JsonElement>> Execute,
    Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>>? Render = null,
    bool PersistMeta = true,
    Func<JsonElement, JsonElement>? MetaOf = null);

