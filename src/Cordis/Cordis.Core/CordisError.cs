namespace Cordis.Core;

/// <summary>Stable machine-readable Cordis error codes (port of the TS CordisError.Code table).</summary>
public static class CordisErrorCode
{
    /// <summary>Raised when an effect or registration is attempted on a disposed or unloading fiber.</summary>
    public const string INACTIVE_EFFECT = "INACTIVE_EFFECT";
}

/// <summary>
/// Framework error with a stable machine-readable <see cref="Code"/> (port of the vendored Cordis
/// CordisError). The default message is the code's canonical text; pass an explicit message to
/// override it.
/// </summary>
public class CordisError : Exception
{
    /// <summary>The stable error code.</summary>
    public string Code { get; }

    /// <summary>Create an error with the canonical message for <paramref name="code"/>.</summary>
    public CordisError(string code)
        : base(CodeToMessage(code))
    {
        Code = code;
    }

    /// <summary>Create an error with an explicit human-readable message.</summary>
    public CordisError(string code, string message)
        : base(message)
    {
        Code = code;
    }

    private static string CodeToMessage(string code) => code switch
    {
        CordisErrorCode.INACTIVE_EFFECT => "cannot create effect on inactive context",
        _ => code,
    };
}
