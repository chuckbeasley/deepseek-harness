namespace Harness.Workspace;

/// <summary>
/// The workspace capability Service Definition (port of the workspace capability seam of
/// packages/workspace/workspace): one current workspace over an existing directory, with an
/// open/close lifecycle. The durable registry, session membership accounting, and header-validated
/// ordering of the TS <c>WorkspaceRegistry</c> are deferred; this phase ports the identity/root and
/// lifecycle core the registry is built on.
/// </summary>
public interface IWorkspaceService
{
    /// <summary>
    /// Open (or reuse) the workspace for an existing directory. The path is canonicalized to an
    /// absolute normalized form; a nonexistent path rejects with WORKSPACE_NOT_FOUND and a
    /// non-directory rejects with WORKSPACE_NOT_DIRECTORY. Opening the same canonical path twice
    /// returns the existing workspace without changing its title; opening a different path while a
    /// workspace is already open rejects with WORKSPACE_ALREADY_OPEN (the lifecycle holds one
    /// current workspace — close it first).
    /// </summary>
    /// <param name="path">existing directory to own, in any path spelling.</param>
    /// <param name="title">display title used only when a new workspace is created; defaults to the directory name.</param>
    /// <returns>the existing or newly opened workspace.</returns>
    Workspace Open(string path, string? title = null);

    /// <summary>The currently open workspace, or <c>null</c> when none is open.</summary>
    Workspace? Current { get; }

    /// <summary>The canonical root of the currently open workspace, or <c>null</c> when none is open.</summary>
    string? CurrentRoot { get; }

    /// <summary>Close the current workspace and release the lifecycle slot. Idempotent.</summary>
    void Close();
}
