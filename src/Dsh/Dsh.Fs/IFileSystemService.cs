namespace Dsh.Fs;

/// <summary>
/// Filesystem Service Definition for one execution world (wave-1 C# port of
/// packages/fs/fs). A consumer never passes raw paths to the operations: it builds a request
/// record, calls the matching explicit resolve(request): spec step — where path validation,
/// workspace containment, and defaulting live — and runs the returned spec. There are no hidden
/// defaults inside the run methods; in particular the write intent is materialized by
/// <see cref="ResolveWrite"/>.
///
/// Wave-1 surface vs the TS seam: readText/writeText/editText/readBytes/list/stat/delete/mkdir are
/// ported; streamText, lstat, contains, processPath, and fileUrl are deferred, as are the
/// fs/observed + fs/write-intent policy events (the observation gate lives in the consumer tools;
/// the fs-observation-policy seam is named in the provider docs but not ported). The consumer
/// tools (read/write/edit) are non-durable: they append no session events. The local provider
/// pins every path inside one workspace root, which the TS fs-local backend does not
/// (containment is a port decision, not a TS fact).
/// </summary>
public interface IFileSystemService
{
    /// <summary>Explicit resolve(request): spec step for a text read.</summary>
    FsReadSpec ResolveRead(FsReadRequest request);

    /// <summary>Explicit resolve(request): spec step for a raw-bytes read; MaxBytes must be positive.</summary>
    FsReadBytesSpec ResolveReadBytes(FsReadBytesRequest request);

    /// <summary>Explicit resolve(request): spec step for a write; an omitted intent becomes <see cref="FsUnconditionalIntent"/>.</summary>
    FsWriteSpec ResolveWrite(FsWriteRequest request);

    /// <summary>Explicit resolve(request): spec step for a literal edit; an omitted version guard stays null.</summary>
    FsEditSpec ResolveEdit(FsEditRequest request);

    /// <summary>Explicit resolve(request): spec step for a listing.</summary>
    FsListSpec ResolveList(FsListRequest request);

    /// <summary>Explicit resolve(request): spec step for metadata.</summary>
    FsStatSpec ResolveStat(FsStatRequest request);

    /// <summary>Explicit resolve(request): spec step for a delete.</summary>
    FsDeleteSpec ResolveDelete(FsDeleteRequest request);

    /// <summary>Explicit resolve(request): spec step for a mkdir.</summary>
    FsMkdirSpec ResolveMkdir(FsMkdirRequest request);

    /// <summary>Return target metadata, or <c>null</c> when the target is absent.</summary>
    Task<FsInfo?> StatAsync(FsStatSpec spec, CancellationToken ct = default);

    /// <summary>Read the whole regular UTF-8 text file as one decoded string; binary and invalid UTF-8 fail with FS_NOT_TEXT.</summary>
    Task<string> ReadTextAsync(FsReadSpec spec, CancellationToken ct = default);

    /// <summary>Read the whole regular file as raw bytes with no decoding; content above <c>MaxBytes</c> fails with FS_TOO_LARGE.</summary>
    Task<byte[]> ReadBytesAsync(FsReadBytesSpec spec, CancellationToken ct = default);

    /// <summary>List direct children of a directory in stable name order; listings never read file contents.</summary>
    Task<IReadOnlyList<FsDirEntry>> ListAsync(FsListSpec spec, CancellationToken ct = default);

    /// <summary>Atomically create or replace UTF-8 text under the spec's materialized intent.</summary>
    Task<FsWriteOutcome> WriteTextAsync(FsWriteSpec spec, CancellationToken ct = default);

    /// <summary>Atomically apply one literal text edit under the spec's observed version guard (null = unconditional).</summary>
    Task<FsEditOutcome> EditTextAsync(FsEditSpec spec, CancellationToken ct = default);

    /// <summary>Remove a file or an empty directory; a missing target fails with FS_NOT_FOUND.</summary>
    Task DeleteAsync(FsDeleteSpec spec, CancellationToken ct = default);

    /// <summary>Create a directory and any missing parents; an existing file at the path fails with FS_NOT_DIRECTORY.</summary>
    Task MkdirAsync(FsMkdirSpec spec, CancellationToken ct = default);
}
