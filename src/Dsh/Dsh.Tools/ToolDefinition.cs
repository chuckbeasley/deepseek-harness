using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Tools;

/// <summary>
/// Runtime context handed to a tool body after the registry has accepted a call. Part 1 carries
/// only identity, arguments, and cancellation; the registry and richer context arrive in part 2.
/// </summary>
public sealed record ToolRunContext(ToolCallId CallId, string Name, JsonElement Arguments, CancellationToken CancellationToken);

/// <summary>
/// A registered tool: its model-facing schema plus the execution function. The registry
/// (<c>ToolRuntime</c>) that registers, presents, and executes these arrives in part 2.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    JsonElement OutputSchema,
    Func<JsonElement, ToolRunContext, Task<JsonElement>> Execute,
    Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>>? Render = null);
