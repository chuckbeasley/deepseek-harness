namespace Harness.Context;

/// <summary>One named text contribution produced by a context contributor.</summary>
public sealed record ContextSection(string Key, string Text);

/// <summary>Loud failure resolving a file reference; the message names the offending path.</summary>
public sealed class FileReferenceError : Exception
{
    /// <summary>Create the error for the failed <paramref name="reference"/>.</summary>
    public FileReferenceError(string reference, string detail)
        : base($"file reference \"{reference}\" {detail}")
    {
        Reference = reference;
    }

    /// <summary>The offending reference as written in the message.</summary>
    public string Reference { get; }
}

/// <summary>Stable machine codes for session-reference failures (port of the TS SessionReferenceErrorCode vocabulary).</summary>
public static class SessionReferenceErrorCodes
{
    public const string InvalidReference = "SESSION_REFERENCE_INVALID_REFERENCE";

    public const string SelfReference = "SESSION_REFERENCE_SELF_REFERENCE";

    public const string TooMany = "SESSION_REFERENCE_TOO_MANY";

    public const string ReadFailed = "SESSION_REFERENCE_READ_FAILED";
}

/// <summary>Loud failure preparing a session reference (port of SessionReferenceError).</summary>
public sealed class SessionReferenceError : Exception
{
    /// <summary>Create the error with a stable <paramref name="code"/> and an optional chained cause.</summary>
    public SessionReferenceError(string message, string code, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="SessionReferenceErrorCodes"/>).</summary>
    public string Code { get; }
}
