namespace Harness.Spill;

/// <summary>
/// The spill storage capability Service Definition (C# port of the spill seam of
/// packages/spill/spill + spill-local): a spill file registry under one spill root. A spill file is
/// a private, session-scoped artifact for a tool's oversized text; the returned <see cref="SpillFile.Path"/>
/// is the model-facing locator. The TS <c>SpillStore.saveText</c> surface is ported as
/// <see cref="Claim"/>; the registry adds register/release/list so callers can admit pre-existing
/// spill paths and release them. Retention policy (packages/spill/spill-policy) is deferred and
/// named here: this seam owns no policy beyond root containment and provider-scoped cleanup.
/// </summary>
public interface ISpillService
{
    /// <summary>
    /// Claim a new spill file under the spill root: the content is written to a fresh, exclusive
    /// file under the session-scoped directory (the caller-suggested name is sanitized to one safe
    /// path segment and never becomes a path), and the file is registered. Fails loud on a real
    /// storage failure — the caller decides how to degrade.
    /// </summary>
    /// <param name="sessionId">owning session id; selects the private session-scoped directory.</param>
    /// <param name="suggestedName">a caller-suggested base name, treated as a hint, never a path.</param>
    /// <param name="content">the full text to persist (UTF-8).</param>
    /// <returns>the registered spill file with its path and byte length.</returns>
    SpillFile Claim(string sessionId, string suggestedName, string content);

    /// <summary>
    /// Register an existing spill path: the resolved absolute path must live inside the spill root
    /// (containment is enforced before existence), must name an existing regular file, and must not
    /// already be registered. Used to admit spill files created outside this process.
    /// </summary>
    /// <param name="path">an existing spill file path, in any spelling.</param>
    /// <returns>the registered spill file.</returns>
    SpillFile Register(string path);

    /// <summary>
    /// Release one registered spill file: its registration is removed and the file is deleted. A
    /// file that is already gone is not a failure (idempotent removal, mirroring the TS
    /// <c>unlinkIdempotent</c>); returns whether a registration was removed.
    /// </summary>
    /// <param name="path">the registered spill file path.</param>
    /// <returns><c>true</c> when the path was registered and is now released.</returns>
    bool Release(string path);

    /// <summary>The registered spill files, in registration order (a snapshot).</summary>
    IReadOnlyList<SpillFile> List();

    /// <summary>
    /// Best-effort sweep of the spill root: regular files under <c>session-&lt;12 hex&gt;</c>
    /// directories whose last-write time is strictly older than <paramref name="maxAge"/> are
    /// deleted, emptied session directories are pruned, and registry entries whose file vanished
    /// are dropped. Symlinks are never followed; every per-entry failure is contained, so this
    /// never throws (mirrors the TS <c>sweepSpillRoots</c> contract).
    /// </summary>
    /// <param name="maxAge">age cutoff; a file written exactly at the boundary is kept.</param>
    void Cleanup(TimeSpan maxAge);
}
