namespace Dsh.Llm;

/// <summary>Typed error for LLM-related failures with a stable machine-readable code.</summary>
public sealed class LlmError : Exception
{
    /// <summary>Create the error; <paramref name="code"/> is the stable provider-neutral machine code.</summary>
    public LlmError(string message, string code)
        : this(message, code, null)
    {
    }

    /// <summary>
    /// Create the error with the provider HTTP status when one exists; <paramref name="code"/> is
    /// the stable provider-neutral machine code. The status travels on <see cref="Failure"/> so
    /// retry policy can distinguish retryable provider responses without parsing the message.
    /// </summary>
    public LlmError(string message, string code, int? status)
        : base(message)
    {
        Code = code;
        Failure = new LlmFailure(message, code, status);
    }

    /// <summary>Stable provider-neutral machine-routing code.</summary>
    public string Code { get; }

    /// <summary>Serializable failure facts.</summary>
    public LlmFailure Failure { get; }
}
