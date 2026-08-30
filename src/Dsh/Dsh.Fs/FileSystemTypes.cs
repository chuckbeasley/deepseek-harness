using System.Text.Json.Serialization;

namespace Dsh.Fs;

/// <summary>
/// Opaque key for stale guards and target lookup (port of the TS <c>FsTargetKey</c> brand).
/// The local backend uses the normalized absolute path; consumers MUST NOT parse it or assume it
/// is a local path — only <see cref="FsTarget.DisplayPath"/> is model-facing.
/// </summary>
public readonly record struct FsTargetKey(string Value)
{
    public static implicit operator string(FsTargetKey key) => key.Value;

    public override string ToString() => Value;
}

/// <summary>Opaque file-version token — the freshness token a guarded write checks against (port of the TS <c>FsVersion</c> brand).</summary>
public readonly record struct FsVersion(string Value)
{
    public static implicit operator string(FsVersion version) => version.Value;

    public override string ToString() => Value;
}

/// <summary>
/// A path resolved by the provider into a stable identity (port of the TS <c>FsTarget</c>).
/// resolve() produces this; every operation takes it. <see cref="TargetKey"/> is the opaque
/// backend key; <see cref="DisplayPath"/> is the workspace-relative path shown to the model/UI.
/// </summary>
public sealed record FsTarget(FsTargetKey TargetKey, string DisplayPath);

/// <summary>What a filesystem entry is (port of the TS <c>type</c> vocabulary).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FsPathType
{
    /// <summary>A regular file.</summary>
    [JsonStringEnumMemberName("file")] File,

    /// <summary>A directory.</summary>
    [JsonStringEnumMemberName("directory")] Directory,

    /// <summary>Anything else (device, socket, ...).</summary>
    [JsonStringEnumMemberName("other")] Other,
}

/// <summary>Metadata about a target — what stat returns; <c>null</c> from the provider means the target is absent.</summary>
public sealed record FsInfo(FsVersion Version, FsPathType Type, long? Size);

/// <summary>One direct child returned by list, with a resolved target plus cheap metadata; listings never read file contents.</summary>
public sealed record FsDirEntry(string Name, FsPathType Type, FsTarget Target, FsVersion? Version = null, long? Size = null);

/// <summary>
/// Guarded write intent (port of the TS <c>FsWriteIntent</c>). The request side omits an intent
/// for an unconditional create-or-overwrite — that is NOT a third request arm; the write spec
/// materializes the default as <see cref="FsUnconditionalIntent"/> in the explicit resolve step.
/// </summary>
public abstract record FsWriteIntent
{
    /// <summary>Discriminant tag: "createIfAbsent", "replaceIfVersion", or the resolved "unconditional".</summary>
    public abstract string Kind { get; }
}

/// <summary>Reject an existing target with FS_NOT_OBSERVED.</summary>
public sealed record FsCreateIfAbsentIntent : FsWriteIntent
{
    public override string Kind => "createIfAbsent";
}

/// <summary>Reject an absent target or a version mismatch with FS_STALE_VERSION.</summary>
public sealed record FsReplaceIfVersionIntent(FsVersion Version) : FsWriteIntent
{
    public override string Kind => "replaceIfVersion";
}

/// <summary>Spec-layer default: unconditional atomic create-or-overwrite.</summary>
public sealed record FsUnconditionalIntent : FsWriteIntent
{
    public override string Kind => "unconditional";
}

/// <summary>Outcome of a full-file write (port of the TS <c>FsWriteOutcome</c>).</summary>
public sealed record FsWriteOutcome(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("version")] FsVersion Version,
    [property: JsonPropertyName("before")] string? Before,
    [property: JsonPropertyName("after")] string After);

// --- Consumer requests and resolved specs ---
//
// A consumer never passes a raw path to an operation: it builds a request record and calls the
// matching explicit resolve(request): spec step, which validates the path, enforces workspace
// containment, and applies defaults. Operations run only against resolved specs.

/// <summary>Consumer request for a text read: the raw path only; the resolve step validates it.</summary>
public sealed record FsReadRequest(string Path);

/// <summary>Consumer request for a raw-bytes read: the raw path plus the inclusive byte cap.</summary>
public sealed record FsReadBytesRequest(string Path, long MaxBytes);

/// <summary>
/// Consumer request for a full-file text write. <see cref="Intent"/> is optional: omitting it
/// means unconditional create-or-overwrite; the explicit resolve step materializes the default.
/// </summary>
public sealed record FsWriteRequest(string Path, string Content, FsWriteIntent? Intent = null);

/// <summary>Consumer request for a directory listing.</summary>
public sealed record FsListRequest(string Path);

/// <summary>Consumer request for target metadata.</summary>
public sealed record FsStatRequest(string Path);

/// <summary>Consumer request to remove a file or an empty directory.</summary>
public sealed record FsDeleteRequest(string Path);

/// <summary>Consumer request to create a directory (and any missing parents).</summary>
public sealed record FsMkdirRequest(string Path);

/// <summary>Resolved text-read spec: the stable target the read runs against.</summary>
public sealed record FsReadSpec(FsTarget Target);

/// <summary>Resolved raw-bytes-read spec: the stable target plus the enforced byte cap.</summary>
public sealed record FsReadBytesSpec(FsTarget Target, long MaxBytes);

/// <summary>Resolved write spec: stable target, content, and the materialized write intent.</summary>
public sealed record FsWriteSpec(FsTarget Target, string Content, FsWriteIntent Intent);

/// <summary>Resolved listing spec.</summary>
public sealed record FsListSpec(FsTarget Target);

/// <summary>Resolved stat spec.</summary>
public sealed record FsStatSpec(FsTarget Target);

/// <summary>Resolved delete spec.</summary>
public sealed record FsDeleteSpec(FsTarget Target);

/// <summary>Resolved mkdir spec.</summary>
public sealed record FsMkdirSpec(FsTarget Target);

/// <summary>
/// Stable, machine-routable codes for filesystem failures (port of the TS <c>FsErrorCode</c>
/// vocabulary). Carried on <see cref="FsError"/>; retry/permission/UI layers branch on the code
/// without parsing messages.
/// </summary>
public static class FsErrorCodes
{
    public const string NotFound = "FS_NOT_FOUND";

    public const string NotDirectory = "FS_NOT_DIRECTORY";

    public const string NotText = "FS_NOT_TEXT";

    public const string NotRegularFile = "FS_NOT_REGULAR_FILE";

    public const string TooLarge = "FS_TOO_LARGE";

    public const string PermissionDenied = "FS_PERMISSION_DENIED";

    public const string SandboxDenied = "FS_SANDBOX_DENIED";

    public const string IoError = "FS_IO_ERROR";

    public const string StaleVersion = "FS_STALE_VERSION";

    public const string NotObserved = "FS_NOT_OBSERVED";

    public const string AmbiguousEdit = "FS_AMBIGUOUS_EDIT";

    public const string EditNotFound = "FS_EDIT_NOT_FOUND";

    public const string Aborted = "FS_ABORTED";
}

/// <summary>
/// Typed filesystem failure (port of the TS <c>FsError</c>): a message plus a stable
/// <see cref="Code"/> from <see cref="FsErrorCodes"/>. Providers and tools raise the same codes
/// instead of inventing message strings.
/// </summary>
public sealed class FsError : Exception
{
    public FsError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="FsErrorCodes"/>).</summary>
    public string Code { get; }
}
