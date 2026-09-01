using System.Text.Json.Serialization;

namespace Harness.Llm;

/// <summary>Serializable provider or transport failure facts; policy decides whether they are retryable.</summary>
public sealed record LlmFailure(
    [property: JsonPropertyOrder(0)] string Message,
    [property: JsonPropertyOrder(1)] string Code,
    int? Status = null);
