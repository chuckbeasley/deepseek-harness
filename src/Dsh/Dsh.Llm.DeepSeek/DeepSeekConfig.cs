namespace Harness.Llm.DeepSeek;

/// <summary>Thinking-effort levels accepted by the DeepSeek chat-completions endpoint.</summary>
public enum DeepSeekReasoningEffort
{
    /// <summary>Disables thinking for the request.</summary>
    Off,

    /// <summary>Routine or latency-sensitive tasks.</summary>
    Low,

    /// <summary>The default balance for most tasks.</summary>
    High,

    /// <summary>The hardest quality-first tasks.</summary>
    Max,
}

/// <summary>
/// DeepSeek provider connection facts. Every field is optional in configuration: a missing API key
/// resolves through <see cref="ApiKeyEnv"/> at each request (a request with no key anywhere fails
/// with <c>MISSING_CREDENTIAL</c>), an omitted endpoint falls back to <c>$DEEPSEEK_BASE_URL</c> then
/// the public API, and an omitted thinking mode uses the provider default. The adapter validates
/// the cross-field thinking/effort contract at construction.
/// </summary>
public sealed record DeepSeekConfig(
    string? ApiKey = null,
    string? ApiKeyEnv = null,
    string? BaseUrl = null,
    bool? Thinking = null,
    DeepSeekReasoningEffort? ReasoningEffort = null);
