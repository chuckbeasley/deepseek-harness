using System.Text.Json;

namespace Dsh.Llm;

/// <summary>
/// Adapter-private lossless-JSON state for replaying a successful response, carried by a terminal
/// finish chunk and stored on the assembled assistant message's model source.
/// </summary>
public sealed record ReplayEnvelope(JsonElement Response, JsonElement[]? Blocks = null);
