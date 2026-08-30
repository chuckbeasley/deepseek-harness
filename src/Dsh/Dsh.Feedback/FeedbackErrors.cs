namespace Dsh.Feedback;

/// <summary>Stable error codes for rejected feedback writes (TS <c>MessageFeedbackFailure</c> codes).</summary>
public static class FeedbackErrorCode
{
    public const string NoteBlank = "note-blank";
    public const string NoteTooLarge = "note-too-large";
}

/// <summary>Error returned by the feedback domain boundary with a stable machine-routable code.</summary>
public sealed class FeedbackError : Exception
{
    /// <summary>Create the failure; <paramref name="code"/> is one of <see cref="FeedbackErrorCode"/>.</summary>
    public FeedbackError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine-routable classification.</summary>
    public string Code { get; }
}
