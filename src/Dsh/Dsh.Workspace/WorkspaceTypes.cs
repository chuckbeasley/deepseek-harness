namespace Dsh.Workspace;

using Dsh.Session;

/// <summary>
/// Stable identifier of one workspace (port of the TS <c>WorkspaceId</c> brand). A generated uuid,
/// never the path: path normalization rewrites paths, and a reference anchor must stay stable.
/// </summary>
public readonly record struct WorkspaceId(string Value)
{
    public static implicit operator string(WorkspaceId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Live directory state of one workspace (port of the TS <c>status()</c> union).</summary>
public enum WorkspaceStatus
{
    /// <summary>The workspace directory currently exists.</summary>
    Ok,

    /// <summary>The workspace directory is currently absent; the record is untouched.</summary>
    MissingDir,
}

/// <summary>
/// One workspace: a stable id over an existing directory, a display title, creation/update
/// instants, and its accounted session membership (port of the TS <c>Workspace</c> record core —
/// the membership arrives with the session/workspace attach flows).
/// <see cref="Root"/> is the canonical absolute directory path stamped at open; it is
/// never rewritten afterwards, even when the directory disappears (see <see cref="Status"/>).
/// </summary>
public sealed record Workspace(
    WorkspaceId Id,
    string Root,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SessionId>? SessionIds = null)
{
    /// <summary>The accounted session membership; empty when none was declared.</summary>
    public IReadOnlyList<SessionId> SessionIdsOrEmpty => SessionIds ?? Array.Empty<SessionId>();

    /// <summary>
    /// Live directory check, uncached: whether <see cref="Root"/> currently exists. A missing
    /// directory never mutates the record — the directory may only be temporarily moved.
    /// </summary>
    /// <returns><see cref="WorkspaceStatus.Ok"/> when the directory exists, <see cref="WorkspaceStatus.MissingDir"/> otherwise.</returns>
    public WorkspaceStatus Status() => Directory.Exists(Root) ? WorkspaceStatus.Ok : WorkspaceStatus.MissingDir;
}
