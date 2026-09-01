namespace Dsh.Llm;

/// <summary>
/// A single model request, fully assembled. <see cref="SessionId"/> is the owning session's id
/// for loop-built requests; it travels as a plain string so Dsh.Llm keeps no reference on
/// Dsh.Session. Dsh.AgentLoop sets it on every request it builds and its invariant verifies the
/// request against that live session's log. <see cref="Purpose"/> marks auxiliary calls (the TS
/// <c>purpose</c> union: <c>"compaction"</c> summarization) that ride the same session binding
/// but are exempt from the loop-request reconstruction invariant.
/// </summary>
public sealed record GenerateOptions(
    string Provider,
    string Model,
    IReadOnlyList<Message> Messages,
    string? System = null,
    IReadOnlyList<ToolSchema>? Tools = null,
    double? Temperature = null,
    int? MaxTokens = null,
    CancellationToken CancellationToken = default,
    string? SessionId = null,
    string? Purpose = null);
