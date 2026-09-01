using System.Text.Json;

namespace Harness.Llm;

/// <summary>JSON-schema description of a tool, as sent to the model.</summary>
public sealed record ToolSchema(string Name, string Description, JsonElement Parameters);
