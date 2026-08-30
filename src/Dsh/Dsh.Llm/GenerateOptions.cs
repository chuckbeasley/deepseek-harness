namespace Dsh.Llm;

/// <summary>
/// A single model request, fully assembled. The spike drops the TS sessionId/purpose fields
/// (they would create a Dsh.Llm -> Dsh.Session edge); they return with the full loop in part 2.
/// </summary>
public sealed record GenerateOptions(
    string Provider,
    string Model,
    IReadOnlyList<Message> Messages,
    string? System = null,
    IReadOnlyList<ToolSchema>? Tools = null,
    double? Temperature = null,
    int? MaxTokens = null,
    CancellationToken CancellationToken = default);
