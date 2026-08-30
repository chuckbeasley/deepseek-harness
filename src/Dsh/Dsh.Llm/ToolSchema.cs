using System.Text.Json;

namespace Dsh.Llm;

/// <summary>JSON-schema description of a tool, as sent to the model.</summary>
public sealed record ToolSchema(string Name, string Description, JsonElement Parameters);
