using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Tools;

/// <summary>
/// A registered tool: its model-facing schema plus the execution function. The registry
/// (<c>ToolRuntime</c>) registers, presents, and executes these; <see cref="ToolRunContext"/>
/// lives in ToolExecution.cs.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    JsonElement OutputSchema,
    Func<JsonElement, ToolRunContext, Task<JsonElement>> Execute,
    Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>>? Render = null);

