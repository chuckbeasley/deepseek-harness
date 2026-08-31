using Cordis.Core;

namespace Dsh.Fs;

/// <summary>Configuration for the local filesystem backend: the single workspace root all paths resolve inside.</summary>
public sealed record FsProviderConfig(string Root);

/// <summary>
/// Host-filesystem provider for ctx.fs (wave-1 port of packages/fs/fs-local). All paths resolve
/// inside one workspace root; a path that escapes the root fails loud with FS_SANDBOX_DENIED.
/// (The TS fs-local backend is unbounded and leaves containment to a sandbox backend or a
/// permission plugin — the port makes the root a hard boundary.) Reads expose regular UTF-8
/// text or typed <see cref="FsError"/>s, writes stage an owner-only temp file in the target
/// directory and publish by rename, and every failure carries a stable code.
///
/// Deviations from fs-local: the target key is the lexical normalized absolute path (no realpath
/// identity); the version token derives from .NET metadata (length + last-write + creation
/// ticks) instead of dev:ino:mtimeNs; list order is ordinal rather than locale; the write
/// outcome's <c>before</c> diff basis is always <c>null</c> (diff/edit deferred); cancellation
/// surfaces as FS_ABORTED like the TS seam.
/// </summary>
public sealed class LocalFileSystemProvider : Service, IFileSystemService
{
    private const int BinarySampleBytes = 8192;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _root;

    /// <summary>Register the provider as ctx.fs over <paramref name="config"/>.Root; the root directory is created when missing.</summary>
    public LocalFileSystemProvider(Context ctx, FsProviderConfig config)
        : base(ctx, "fs")
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Root))
        {
            throw new ArgumentException("workspace root must be a non-empty path", nameof(config));
        }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root));
        Directory.CreateDirectory(_root);
    }

    /// <summary>The normalized workspace root every resolved target lives inside.</summary>
    public string WorkspaceRoot => _root;

    // --- resolve(request): spec steps ---

    /// <inheritdoc />
    public FsReadSpec ResolveRead(FsReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FsReadSpec(ResolveTarget(request.Path));
    }

    /// <inheritdoc />
    public FsReadBytesSpec ResolveReadBytes(FsReadBytesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxBytes < 1)
        {
            throw new ArgumentException("maxBytes must be a positive integer", nameof(request));
        }
        return new FsReadBytesSpec(ResolveTarget(request.Path), request.MaxBytes);
    }

    /// <inheritdoc />
    public FsWriteSpec ResolveWrite(FsWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Explicit defaulting: an omitted intent means unconditional create-or-overwrite.
        return new FsWriteSpec(ResolveTarget(request.Path), request.Content, request.Intent ?? new FsUnconditionalIntent());
    }

    /// <inheritdoc />
    public FsListSpec ResolveList(FsListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FsListSpec(ResolveTarget(request.Path));
    }

    /// <inheritdoc />
    public FsStatSpec ResolveStat(FsStatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FsStatSpec(ResolveTarget(request.Path));
    }

    /// <inheritdoc />
    public FsDeleteSpec ResolveDelete(FsDeleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FsDeleteSpec(ResolveTarget(request.Path));
    }

    /// <inheritdoc />
    public FsMkdirSpec ResolveMkdir(FsMkdirRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FsMkdirSpec(ResolveTarget(request.Path));
    }

    // --- operations ---

    /// <inheritdoc />
    public Task<FsInfo?> StatAsync(FsStatSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "stat");
        return Task.FromResult(Probe(spec.Target.TargetKey, spec.Target.DisplayPath));
    }

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(FsReadSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "read");
        var display = spec.Target.DisplayPath;
        var info = Probe(spec.Target.TargetKey, display)
            ?? throw new FsError($"cannot read \"{display}\": not found", FsErrorCodes.NotFound);
        if (info.Type != FsPathType.File)
        {
            throw new FsError($"cannot read \"{display}\": not a regular file", FsErrorCodes.NotRegularFile);
        }
        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(spec.Target.TargetKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new FsError("read aborted", FsErrorCodes.Aborted);
        }
        catch (Exception error)
        {
            throw IoError("read", display, error);
        }
        var sample = raw.AsSpan(0, Math.Min(raw.Length, BinarySampleBytes));
        if (sample.IndexOf((byte)0) >= 0)
        {
            throw new FsError($"cannot read \"{display}\": binary file", FsErrorCodes.NotText);
        }
        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException error)
        {
            throw new FsError($"cannot read \"{display}\": invalid UTF-8 text", FsErrorCodes.NotText, error);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadBytesAsync(FsReadBytesSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "read");
        var display = spec.Target.DisplayPath;
        var info = Probe(spec.Target.TargetKey, display)
            ?? throw new FsError($"cannot read \"{display}\": not found", FsErrorCodes.NotFound);
        if (info.Type != FsPathType.File)
        {
            throw new FsError($"cannot read \"{display}\": not a regular file", FsErrorCodes.NotRegularFile);
        }
        if (info.Size is long size && size > spec.MaxBytes)
        {
            throw new FsError($"cannot read \"{display}\": {size} bytes exceeds the {spec.MaxBytes}-byte limit", FsErrorCodes.TooLarge);
        }
        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(spec.Target.TargetKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new FsError("read aborted", FsErrorCodes.Aborted);
        }
        catch (Exception error)
        {
            throw IoError("read", display, error);
        }
        if (raw.Length > spec.MaxBytes)
        {
            throw new FsError($"cannot read \"{display}\": content exceeds the {spec.MaxBytes}-byte limit", FsErrorCodes.TooLarge);
        }
        return raw;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FsDirEntry>> ListAsync(FsListSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "list");
        var display = spec.Target.DisplayPath;
        var info = Probe(spec.Target.TargetKey, display)
            ?? throw new FsError($"cannot list \"{display}\": not found", FsErrorCodes.NotFound);
        if (info.Type != FsPathType.Directory)
        {
            throw new FsError($"cannot list \"{display}\": not a directory", FsErrorCodes.NotDirectory);
        }
        var entries = new List<FsDirEntry>();
        try
        {
            foreach (var name in Directory.EnumerateFileSystemEntries(spec.Target.TargetKey)
                         .Select(Path.GetFileName)
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                ThrowIfAborted(ct, "list");
                var childKey = Path.Combine(spec.Target.TargetKey, name!);
                var childDisplay = ChildDisplayPath(display, name!);
                var childInfo = Probe(childKey, childDisplay);
                entries.Add(new FsDirEntry(
                    name!,
                    childInfo?.Type ?? FsPathType.Other,
                    new FsTarget(new FsTargetKey(childKey), childDisplay),
                    childInfo?.Version,
                    childInfo is { Type: FsPathType.File } ? childInfo.Size : null));
            }
        }
        catch (FsError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw IoError("list", display, error);
        }
        return Task.FromResult<IReadOnlyList<FsDirEntry>>(entries);
    }

    /// <inheritdoc />
    public async Task<FsWriteOutcome> WriteTextAsync(FsWriteSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "write");
        var key = spec.Target.TargetKey;
        var display = spec.Target.DisplayPath;
        var existing = Probe(key, display);

        if (spec.Intent is FsReplaceIfVersionIntent replace)
        {
            if (existing is null)
            {
                throw new FsError($"cannot write \"{display}\": file no longer exists", FsErrorCodes.StaleVersion);
            }
            if (existing.Version != replace.Version)
            {
                throw new FsError($"cannot write \"{display}\": file changed since it was read", FsErrorCodes.StaleVersion);
            }
        }
        else if (spec.Intent is FsCreateIfAbsentIntent && existing is not null)
        {
            throw new FsError($"cannot overwrite existing \"{display}\" without reading it first", FsErrorCodes.NotObserved);
        }
        if (existing is not null && existing.Type != FsPathType.File)
        {
            throw new FsError($"cannot write \"{display}\": not a regular file", FsErrorCodes.NotRegularFile);
        }

        var directory = Path.GetDirectoryName(key)
            ?? throw new FsError($"cannot write \"{display}\": invalid path", FsErrorCodes.NotFound);
        var temp = Path.Combine(directory, $".{Path.GetFileName(key)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temp, spec.Content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            if (spec.Intent is FsCreateIfAbsentIntent)
            {
                try
                {
                    File.Move(temp, key);
                }
                catch (IOException) when (File.Exists(key) || Directory.Exists(key))
                {
                    TryDeleteQuietly(temp);
                    throw new FsError($"cannot overwrite existing \"{display}\" without reading it first", FsErrorCodes.NotObserved);
                }
            }
            else
            {
                File.Move(temp, key, overwrite: true);
            }
        }
        catch (FsError)
        {
            TryDeleteQuietly(temp);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDeleteQuietly(temp);
            throw new FsError("write aborted", FsErrorCodes.Aborted);
        }
        catch (Exception error)
        {
            TryDeleteQuietly(temp);
            throw IoError("write", display, error);
        }

        var after = Probe(key, display) ?? new FsInfo(new FsVersion($"missing:{key}"), FsPathType.File, null);
        return new FsWriteOutcome(
            existing is null ? "create" : "update",
            after.Version,
            Before: null,
            After: NormalizeLineEndings(spec.Content));
    }

    /// <inheritdoc />
    public Task DeleteAsync(FsDeleteSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "delete");
        var display = spec.Target.DisplayPath;
        var info = Probe(spec.Target.TargetKey, display)
            ?? throw new FsError($"cannot delete \"{display}\": not found", FsErrorCodes.NotFound);
        try
        {
            if (info.Type == FsPathType.Directory)
            {
                Directory.Delete(spec.Target.TargetKey, recursive: false);
            }
            else
            {
                File.Delete(spec.Target.TargetKey);
            }
        }
        catch (OperationCanceledException)
        {
            throw new FsError("delete aborted", FsErrorCodes.Aborted);
        }
        catch (Exception error)
        {
            throw IoError("delete", display, error);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MkdirAsync(FsMkdirSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfAborted(ct, "mkdir");
        var display = spec.Target.DisplayPath;
        var existing = Probe(spec.Target.TargetKey, display);
        if (existing is not null)
        {
            if (existing.Type != FsPathType.Directory)
            {
                throw new FsError($"cannot mkdir \"{display}\": not a directory", FsErrorCodes.NotDirectory);
            }
            return Task.CompletedTask;
        }
        try
        {
            Directory.CreateDirectory(spec.Target.TargetKey);
        }
        catch (OperationCanceledException)
        {
            throw new FsError("mkdir aborted", FsErrorCodes.Aborted);
        }
        catch (Exception error)
        {
            throw IoError("mkdir", display, error);
        }
        return Task.CompletedTask;
    }

    // --- internals ---

    private FsTarget ResolveTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FsError("file_path must be a non-empty string", FsErrorCodes.NotFound);
        }
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FsError($"cannot resolve \"{path}\": invalid path", FsErrorCodes.NotFound, error);
        }
        if (!IsInsideRoot(full))
        {
            throw new FsError($"path \"{path}\" escapes the workspace root \"{_root}\"", FsErrorCodes.SandboxDenied);
        }
        // The TS surface renders the backend-resolved ABSOLUTE path (the snapshot fixtures
        // tokenize it as {{cwd}}/…), so the display path is the full path, slash-normalized.
        var display = full.Replace('\\', '/');
        return new FsTarget(new FsTargetKey(full), display);
    }

    private bool IsInsideRoot(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        return relative == "."
            || (relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }

    /// <summary>
    /// Probe a target for its version, type, and size. <c>null</c> when the target — or a parent
    /// segment — does not exist; other metadata failures are real permission/IO faults and throw.
    /// </summary>
    private FsInfo? Probe(string absolutePath, string displayPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(absolutePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException error)
        {
            throw new FsError($"cannot stat \"{displayPath}\": permission denied", FsErrorCodes.PermissionDenied, error);
        }
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            return new FsInfo(VersionOf(absolutePath, isDirectory: true), FsPathType.Directory, null);
        }
        long size;
        try
        {
            size = new FileInfo(absolutePath).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new FsError($"cannot stat \"{displayPath}\": {error.Message}", FsErrorCodes.IoError, error);
        }
        return new FsInfo(VersionOf(absolutePath, isDirectory: false), FsPathType.File, size);
    }

    private static FsVersion VersionOf(string absolutePath, bool isDirectory)
    {
        var file = new FileInfo(absolutePath);
        if (isDirectory)
        {
            return new FsVersion($"d:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}");
        }
        return new FsVersion($"f:{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}");
    }

    private static FsError IoError(string verb, string displayPath, Exception error)
    {
        if (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FsError($"cannot {verb} \"{displayPath}\": not found", FsErrorCodes.NotFound, error);
        }
        if (error is UnauthorizedAccessException)
        {
            return new FsError($"cannot {verb} \"{displayPath}\": permission denied", FsErrorCodes.PermissionDenied, error);
        }
        return new FsError($"cannot {verb} \"{displayPath}\": {error.Message}", FsErrorCodes.IoError, error);
    }

    private static void ThrowIfAborted(CancellationToken ct, string verb)
    {
        if (ct.IsCancellationRequested)
        {
            throw new FsError($"{verb} aborted", FsErrorCodes.Aborted);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        // The staged temp is owner-only residue; losing it cannot fail a committed write.
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Swallow: cleanup is best-effort by design.
        }
    }

    private static string ChildDisplayPath(string parentDisplay, string name)
        => parentDisplay.Length == 0 ? name : $"{parentDisplay}/{name}";

    private static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n");
}
