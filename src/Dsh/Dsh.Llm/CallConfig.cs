namespace Dsh.Llm;

/// <summary>Provider, model, reasoning effort, and sampling scalars of one conversation's requests.</summary>
public sealed record LlmCallConfig(
    string Provider,
    string Model,
    ReasoningEffortId? ReasoningEffort = null,
    double? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? Stop = null);

/// <summary>Effective config fields supplied by exact-model adapter resolution rather than by the caller.</summary>
public sealed record LlmCallConfigAdapterDefaults(bool ReasoningEffort = false, bool MaxTokens = false);

/// <summary>Field-wise equality over <see cref="LlmCallConfig"/> (including the Stop list, element-wise).</summary>
public static class CallConfig
{
    public static bool Equals(LlmCallConfig a, LlmCallConfig b)
    {
        if (a.Provider != b.Provider
            || a.Model != b.Model
            || a.ReasoningEffort != b.ReasoningEffort
            || a.Temperature != b.Temperature
            || a.MaxTokens != b.MaxTokens)
        {
            return false;
        }
        if (a.Stop is null || b.Stop is null) return a.Stop is null && b.Stop is null;
        return a.Stop.SequenceEqual(b.Stop);
    }
}
