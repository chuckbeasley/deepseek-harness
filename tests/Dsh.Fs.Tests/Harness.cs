namespace Dsh.Fs.Tests;

/// <summary>
/// One booted fs spine: context, the local filesystem provider over a fresh temp workspace root,
/// and a tool runtime with fs_read/fs_write registered.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public required Context Ctx { get; init; }

    public required LocalFileSystemProvider Fs { get; init; }

    public required ToolRuntime Tools { get; init; }

    public required string WorkspaceRoot { get; init; }

    /// <summary>Boot the spine with a fresh temp workspace root.</summary>
    public static Harness Create()
    {
        var ctx = new Context();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "dsh-fs-tests-" + Guid.NewGuid().ToString("N"));
        var fs = new LocalFileSystemProvider(ctx, new FsProviderConfig(workspaceRoot));
        var tools = new ToolRuntime(ctx);
        tools.Register(FileSystemTools.Read(fs));
        tools.Register(FileSystemTools.Write(fs));
        return new Harness { Ctx = ctx, Fs = fs, Tools = tools, WorkspaceRoot = workspaceRoot };
    }

    /// <summary>Dispose the context (unwinding every effect) and remove the temp workspace root.</summary>
    public ValueTask DisposeAsync()
    {
        Ctx.Dispose();
        if (Directory.Exists(WorkspaceRoot))
        {
            Directory.Delete(WorkspaceRoot, recursive: true);
        }
        return ValueTask.CompletedTask;
    }
}
