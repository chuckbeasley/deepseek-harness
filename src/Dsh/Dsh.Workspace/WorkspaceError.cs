namespace Dsh.Workspace;

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
