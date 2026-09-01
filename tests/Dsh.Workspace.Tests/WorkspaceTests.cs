namespace Harness.Workspace.Tests;

/// <summary>Behavior tests for the workspace capability seam (identity/root + open/close lifecycle).</summary>
public static class WorkspaceTests
{
    /// <summary>Open validates the existing directory, exposes it as the current root, and close releases it.</summary>
    public static void OpenValidatesExistingDirectoryAndLifecycle(Harness h)
    {
        Assert.Null(h.Workspaces.Current);
        Assert.Null(h.Workspaces.CurrentRoot);
        var workspace = h.Workspaces.Open(h.Dir);
        Assert.NotNull(workspace);
        Assert.Same(workspace, h.Workspaces.Current);
        Assert.Equal(h.Dir, h.Workspaces.CurrentRoot);
        Assert.Equal(WorkspaceStatus.Ok, workspace.Status());
        h.Workspaces.Close();
        Assert.Null(h.Workspaces.Current);
        Assert.Null(h.Workspaces.CurrentRoot);
    }

    /// <summary>Opening a nonexistent path fails loud with WORKSPACE_NOT_FOUND.</summary>
    public static void OpenMissingPathFailsLoud(Harness h)
    {
        var error = Assert.Throws<WorkspaceError>(() => h.Workspaces.Open(Path.Combine(h.BaseDir, "nope")));
        Assert.Equal(WorkspaceErrorCodes.NotFound, error.Code);
        Assert.Null(h.Workspaces.Current);
    }

    /// <summary>Opening a regular file fails loud with WORKSPACE_NOT_DIRECTORY.</summary>
    public static void OpenFilePathFailsLoud(Harness h)
    {
        var error = Assert.Throws<WorkspaceError>(() => h.Workspaces.Open(h.FilePath));
        Assert.Equal(WorkspaceErrorCodes.NotDirectory, error.Code);
    }

    /// <summary>Opening an empty path fails loud.</summary>
    public static void OpenEmptyPathFailsLoud(Harness h)
    {
        var error = Assert.Throws<WorkspaceError>(() => h.Workspaces.Open("   "));
        Assert.Equal(WorkspaceErrorCodes.InvalidPath, error.Code);
    }

    /// <summary>The lifecycle holds one current workspace: a second open of a different directory fails loud.</summary>
    public static void SecondOpenDifferentPathFailsLoud(Harness h)
    {
        var other = Path.Combine(h.BaseDir, "other");
        Directory.CreateDirectory(other);
        h.Workspaces.Open(h.Dir);
        var error = Assert.Throws<WorkspaceError>(() => h.Workspaces.Open(other));
        Assert.Equal(WorkspaceErrorCodes.AlreadyOpen, error.Code);
        Assert.Equal(h.Dir, h.Workspaces.CurrentRoot, "the first workspace stays current");
    }

    /// <summary>Reopening the same canonical path (any spelling) returns the same workspace without changing its title.</summary>
    public static void SameCanonicalPathReturnsSameWorkspace(Harness h)
    {
        var first = h.Workspaces.Open(h.Dir);
        var second = h.Workspaces.Open(h.Dir + Path.DirectorySeparatorChar);
        Assert.Same(first, second);
        var canonical = h.Workspaces.Open(Path.Combine(h.Dir, ".", "..", Path.GetFileName(h.Dir)));
        Assert.Same(first, canonical);
        Assert.Equal(h.Dir, first.Root, "the canonical root is the normalized absolute path");
    }

    /// <summary>The live status check reports MissingDir once the directory disappears, without mutating the record.</summary>
    public static void StatusReflectsDirectoryPresence(Harness h)
    {
        var workspace = h.Workspaces.Open(h.Dir);
        Assert.Equal(WorkspaceStatus.Ok, workspace.Status());
        Directory.Delete(h.Dir);
        Assert.Equal(WorkspaceStatus.MissingDir, workspace.Status());
        Assert.Equal(h.Dir, workspace.Root, "the record keeps its canonical root");
    }

    /// <summary>The title defaults to the directory name; an explicit title is honored.</summary>
    public static void TitleDefaultsToDirectoryName(Harness h)
    {
        var workspace = h.Workspaces.Open(h.Dir);
        Assert.Equal("proj", workspace.Title);
        h.Workspaces.Close();
        var other = Path.Combine(h.BaseDir, "other");
        Directory.CreateDirectory(other);
        var named = h.Workspaces.Open(other, "Custom Title");
        Assert.Equal("Custom Title", named.Title);
    }

    /// <summary>A newly opened workspace stamps a stable generated id and creation instants.</summary>
    public static void NewWorkspaceStampsStableIdentity(Harness h)
    {
        var workspace = h.Workspaces.Open(h.Dir);
        Assert.True(workspace.Id.Value.Length == 32, "the id is a generated uuid without separators");
        Assert.True(workspace.CreatedAt <= DateTimeOffset.UtcNow.AddSeconds(1), "createdAt is stamped at open");
        Assert.Equal(workspace.CreatedAt, workspace.UpdatedAt);
    }
}
