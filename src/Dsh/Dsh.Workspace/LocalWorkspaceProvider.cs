using Cordis.Core;

namespace Dsh.Workspace;

/// <summary>
/// Local provider for ctx.workspace (port of the workspace identity/root core of
/// packages/workspace/workspace): validates that the path names an existing directory, canonicalizes
/// it, and holds it as the single current workspace until closed. Path canonicalization is
/// <c>Path.GetFullPath</c> with the trailing separator trimmed — a deviation from the TS
/// <c>fs.realpath</c> canon, which also resolves symlinks (the port documents this: symlinked
/// spellings of one directory are not unified).
/// </summary>
public sealed class LocalWorkspaceProvider : Service, IWorkspaceService
{
    private Workspace? _current;

    /// <summary>Register the provider as ctx.workspace. The provider needs no configuration: the workspace root comes from <see cref="Open"/>.</summary>
    public LocalWorkspaceProvider(Context ctx)
        : base(ctx, "workspace")
    {
    }

    /// <inheritdoc />
    public Workspace? Current => _current;

    /// <inheritdoc />
    public string? CurrentRoot => _current?.Root;

    /// <inheritdoc />
    public Workspace Open(string path, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkspaceError("cannot open a workspace at an empty path", WorkspaceErrorCodes.InvalidPath);
        }
        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceError($"cannot resolve \"{path}\": invalid path", WorkspaceErrorCodes.InvalidPath, error);
        }
        if (File.Exists(canonical))
        {
            throw new WorkspaceError($"cannot create a workspace at '{canonical}': path is not a directory", WorkspaceErrorCodes.NotDirectory);
        }
        if (!Directory.Exists(canonical))
        {
            throw new WorkspaceError($"cannot create a workspace at '{canonical}': path does not exist", WorkspaceErrorCodes.NotFound);
        }
        if (_current is not null)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_current.Root, canonical)) return _current;
            throw new WorkspaceError(
                $"a workspace is already open at '{_current.Root}'; close it before opening another",
                WorkspaceErrorCodes.AlreadyOpen);
        }

        var now = DateTimeOffset.UtcNow;
        var workspace = new Workspace(
            new WorkspaceId(Guid.NewGuid().ToString("N")),
            canonical,
            title ?? Path.GetFileName(canonical),
            now,
            now);
        _current = workspace;
        return workspace;
    }

    /// <inheritdoc />
    public void Close() => _current = null;

    /// <summary>Release the current workspace during context teardown (idempotent with <see cref="Close"/>).</summary>
    public override ValueTask StopAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
