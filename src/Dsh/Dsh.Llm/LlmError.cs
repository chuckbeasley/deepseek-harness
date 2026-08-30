namespace Dsh.Llm;

/// <summary>Typed error for LLM-related failures with a stable machine-readable code.</summary>
public sealed class LlmError : Exception
{
    /// <summary>Create the error; <paramref name="code"/> is the stable provider-neutral machine code.</summary>
    public LlmError(string message, string code)
        : base(message)
    {
        Code = code;
        Failure = new LlmFailure(message, code);
    }

    /// <summary>Stable provider-neutral machine-routing code.</summary>
    public string Code { get; }

    /// <summary>Serializable failure facts.</summary>
    public LlmFailure Failure { get; }
}
