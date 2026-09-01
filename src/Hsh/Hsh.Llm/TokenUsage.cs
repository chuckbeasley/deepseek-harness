namespace Harness.Llm;

/// <summary>Token accounting for one model call (cache fields are optional).</summary>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int? TotalTokens = null,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null,
    int? ReasoningTokens = null);
