namespace Dsh.Workspace.Tests;

/// <summary>
/// One booted workspace spine: a context with the local workspace provider, an existing directory
/// to open, and a sibling non-directory fixture.
/// </summary>
public sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }

    public required LocalWorkspaceProvider Workspaces { get; init; }

    /// <summary>The temp base directory that owns every fixture.</summary>
    public required string BaseDir { get; init; }

    /// <summary>An existing directory ready to open as a workspace.</summary>
    public required string Dir { get; init; }

    /// <summary>An existing regular file (a non-directory open target).</summary>
    public required string FilePath { get; init; }

    /// <summary>Boot the spine with fresh temp fixtures.</summary>
    public static Harness Create()
    {
        var ctx = new Context();
        var baseDir = Path.Combine(Path.GetTempPath(), "dsh-workspace-tests-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(baseDir, "proj");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(baseDir, "not-a-dir.txt");
        File.WriteAllText(filePath, "x");
        var workspaces = new LocalWorkspaceProvider(ctx);
        return new Harness { Ctx = ctx, Workspaces = workspaces, BaseDir = baseDir, Dir = dir, FilePath = filePath };
    }

    /// <summary>Dispose the context and remove the temp base directory.</summary>
    public void Dispose()
    {
        Ctx.Dispose();
        if (Directory.Exists(BaseDir))
        {
            try
            {
                Directory.Delete(BaseDir, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp base is test residue, not a test failure.
            }
        }
    }
}
