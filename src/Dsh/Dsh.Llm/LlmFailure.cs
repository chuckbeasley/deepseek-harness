namespace Dsh.Llm;

/// <summary>Serializable provider or transport failure facts; policy decides whether they are retryable.</summary>
public sealed record LlmFailure(string Message, string Code, int? Status = null);
