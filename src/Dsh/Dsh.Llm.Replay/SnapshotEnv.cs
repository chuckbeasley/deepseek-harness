namespace Dsh.Llm.Replay;

/// <summary>
/// Snapshot-run environment resolution (port of the TS <c>DSH_SNAPSHOT_*</c> contract): the
/// recorded fixture's provider and model route the replay adapter, so runtime rows pick them up
/// exactly as the TS <c>model.cordis.yml</c> patch does.
/// </summary>
public static class SnapshotEnv
{
    /// <summary>Environment variable naming the replay provider route.</summary>
    public const string ProviderEnv = "DSH_SNAPSHOT_PROVIDER";

    /// <summary>Environment variable naming the replay model id.</summary>
    public const string ModelEnv = "DSH_SNAPSHOT_MODEL";

    /// <summary>Environment variable naming the primary fixture path.</summary>
    public const string FileEnv = "DSH_SNAPSHOT_FILE";

    /// <summary>Environment variable naming the optional override sidecar path.</summary>
    public const string OverrideEnv = "DSH_SNAPSHOT_OVERRIDE";

    /// <summary>Environment variable naming the child-session fixture paths (path-separator-delimited).</summary>
    public const string ChildFilesEnv = "DSH_SNAPSHOT_CHILD_FILES";

    /// <summary>The replay provider route, or <c>null</c> outside a snapshot run.</summary>
    public static string? Provider
        => Environment.GetEnvironmentVariable(ProviderEnv) is { Length: > 0 } value ? value : null;

    /// <summary>The replay model id, or <c>null</c> outside a snapshot run.</summary>
    public static string? Model
        => Environment.GetEnvironmentVariable(ModelEnv) is { Length: > 0 } value ? value : null;

    /// <summary>Whether a snapshot fixture is configured (replay or record mode).</summary>
    public static bool IsSnapshotRun => Environment.GetEnvironmentVariable(FileEnv) is { Length: > 0 };
}