using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>
/// Provider, model, reasoning effort, and sampling scalars of one conversation's requests.
/// <see cref="JsonPropertyOrder"/> matches the TS resolved-config key order (provider, model,
/// then maxTokens before reasoningEffort, as the live adapter appends its defaults).
/// </summary>
public sealed record LlmCallConfig(
    [property: JsonPropertyOrder(0)] string Provider,
    [property: JsonPropertyOrder(1)] string Model,
    [property: JsonPropertyOrder(3)] ReasoningEffortId? ReasoningEffort = null,
    [property: JsonPropertyOrder(4)] double? Temperature = null,
    [property: JsonPropertyOrder(2)] int? MaxTokens = null,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string>? Stop = null);

/// <summary>Effective config fields supplied by exact-model adapter resolution rather than by the caller.</summary>
public sealed record LlmCallConfigAdapterDefaults(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ReasoningEffort = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool MaxTokens = false);

/// <summary>Per-model capability metadata a provider adapter exposes for request resolution.</summary>
public sealed record LlmModelMetadata(
    long? ContextWindow = null,
    int? DefaultMaxTokens = null,
    string? DefaultReasoningEffort = null,
    IReadOnlyList<string>? ReasoningEfforts = null,
    IReadOnlyList<string>? InputModalities = null);

/// <summary>Optional adapter capability: exact-model capability lookup for request resolution.</summary>
public interface IAdapterModelMetadata
{
    /// <summary>The exact-model capability metadata, or <c>null</c> for an unknown model.</summary>
    LlmModelMetadata? ResolveModel(string model);
}

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
