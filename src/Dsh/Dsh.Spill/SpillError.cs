namespace Dsh.Spill;

/// <summary>Stable, machine-routable codes for spill storage failures.</summary>
public static class SpillErrorCodes
{
    /// <summary>The path to register is empty, does not exist, or is not a regular file.</summary>
    public const string NotFound = "SPILL_NOT_FOUND";

    /// <summary>The path to register is not a valid spill path (empty or unresolvable).</summary>
    public const string InvalidPath = "SPILL_INVALID_PATH";

    /// <summary>The path to register resolves outside the spill root (containment violation).</summary>
    public const string OutsideRoot = "SPILL_OUTSIDE_ROOT";

    /// <summary>The path is already registered in the spill registry.</summary>
    public const string AlreadyRegistered = "SPILL_ALREADY_REGISTERED";

    /// <summary>A claim or release failed on the underlying medium.</summary>
    public const string IoError = "SPILL_IO_ERROR";
}

/// <summary>Typed spill failure: a message plus a stable <see cref="Code"/> from <see cref="SpillErrorCodes"/>.</summary>
public sealed class SpillError : Exception
{
    public SpillError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="SpillErrorCodes"/>).</summary>
    public string Code { get; }
}
