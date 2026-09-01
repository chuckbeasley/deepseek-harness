namespace Harness.Attachment;

/// <summary>Stable, machine-routable codes for attachment failures (subset of the TS <c>AttachmentErrorCode</c>).</summary>
public static class AttachmentErrorCodes
{
    /// <summary>The source path is empty/unresolvable, the object is missing, or the id is unknown.</summary>
    public const string NotFound = "ATTACHMENT_NOT_FOUND";

    /// <summary>The source path exists but is not a regular file.</summary>
    public const string NotRegularFile = "ATTACHMENT_NOT_REGULAR_FILE";

    /// <summary>The source exceeds the configured byte limit.</summary>
    public const string TooLarge = "ATTACHMENT_TOO_LARGE";

    /// <summary>Stored bytes no longer match the recorded reference (integrity failure).</summary>
    public const string Corrupt = "ATTACHMENT_CORRUPT";

    /// <summary>Persisting or removing an object failed on the underlying medium.</summary>
    public const string WriteFailed = "ATTACHMENT_WRITE_FAILED";

    /// <summary>Reading an object failed on the underlying medium.</summary>
    public const string ReadFailed = "ATTACHMENT_READ_FAILED";
}

/// <summary>
/// Typed attachment failure (port of the TS <c>AttachmentError</c>): a message plus a stable
/// <see cref="Code"/> from <see cref="AttachmentErrorCodes"/>.
/// </summary>
public sealed class AttachmentError : Exception
{
    public AttachmentError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="AttachmentErrorCodes"/>).</summary>
    public string Code { get; }
}
