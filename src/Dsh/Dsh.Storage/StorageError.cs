namespace Harness.Storage;

/// <summary>
/// Stable, machine-routable codes for storage failures (port of the TS <c>StorageErrorCode</c>
/// vocabulary). Carried on <see cref="StorageError"/>; retry/permission/UI layers branch on the
/// code without parsing messages.
/// </summary>
public static class StorageErrorCodes
{
    /// <summary>The stored unit file carries a version different from the opened descriptor's revision.</summary>
    public const string VersionMismatch = "STORAGE_VERSION_MISMATCH";

    /// <summary>The unit file is corrupt, not JSON, or carries a foreign unit header.</summary>
    public const string MalformedMedium = "STORAGE_MALFORMED_MEDIUM";

    /// <summary>An operation was attempted on a closed unit or a closed backend.</summary>
    public const string Closed = "STORAGE_CLOSED";

    /// <summary>A unit or table name does not match <c>[a-z][a-z0-9_]*</c>.</summary>
    public const string InvalidName = "STORAGE_INVALID_NAME";

    /// <summary>The same unit name is already open (a unit has exactly one live handle).</summary>
    public const string AlreadyOpen = "STORAGE_ALREADY_OPEN";

    /// <summary>A write references a table the descriptor did not declare.</summary>
    public const string UndefinedTable = "STORAGE_UNDEFINED_TABLE";

    /// <summary>SetGlobal was called on a unit whose descriptor did not declare <c>HasGlobal</c>.</summary>
    public const string NoGlobalSlot = "STORAGE_NO_GLOBAL_SLOT";

    /// <summary>A publish (atomic replace) failed on the underlying medium.</summary>
    public const string IoError = "STORAGE_IO_ERROR";
}

/// <summary>
/// Typed storage failure (port of the TS <c>StorageError</c>): a message plus a stable
/// <see cref="Code"/> from <see cref="StorageErrorCodes"/>.
/// </summary>
public sealed class StorageError : Exception
{
    public StorageError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="StorageErrorCodes"/>).</summary>
    public string Code { get; }
}
