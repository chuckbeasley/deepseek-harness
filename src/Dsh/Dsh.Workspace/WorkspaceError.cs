namespace Harness.Workspace;

/// <summary>Stable, machine-routable codes for workspace failures.</summary>
public static class WorkspaceErrorCodes
{
    /// <summary>The path to open does not exist (the workspace must point at an existing directory).</summary>
    public const string NotFound = "WORKSPACE_NOT_FOUND";

    /// <summary>The path to open exists but is not a directory.</summary>
    public const string NotDirectory = "WORKSPACE_NOT_DIRECTORY";

    /// <summary>The path is empty or cannot be resolved.</summary>
    public const string InvalidPath = "WORKSPACE_INVALID_PATH";

    /// <summary>A different workspace is already open; the lifecycle holds one current workspace.</summary>
    public const string AlreadyOpen = "WORKSPACE_ALREADY_OPEN";

    /// <summary>A workspace is already registered at the same canonical path.</summary>
    public const string DuplicatePath = "WORKSPACE_DUPLICATE_PATH";

    /// <summary>A rename title is blank or otherwise unusable.</summary>
    public const string InvalidTitle = "WORKSPACE_INVALID_TITLE";

    /// <summary>A rename title is already used by another workspace.</summary>
    public const string NameConflict = "WORKSPACE_NAME_CONFLICT";

    /// <summary>The display-order move names an absent anchor or target.</summary>
    public const string OrderInvalid = "WORKSPACE_ORDER_INVALID";

    /// <summary>The session move names a session that is not a member of the workspace.</summary>
    public const string MoveInvalid = "WORKSPACE_MOVE_INVALID";

    /// <summary>An archive request named a session neither live nor otherwise known.</summary>
    public const string UnknownSession = "WORKSPACE_UNKNOWN_SESSION";
}

/// <summary>Typed workspace failure: a message plus a stable <see cref="Code"/> from <see cref="WorkspaceErrorCodes"/>.</summary>
public sealed class WorkspaceError : Exception
{
    public WorkspaceError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="WorkspaceErrorCodes"/>).</summary>
    public string Code { get; }
}
