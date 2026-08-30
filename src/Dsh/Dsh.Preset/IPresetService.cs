using Cordis.Plugin.Loader;

namespace Dsh.Preset;

/// <summary>
/// One preset directory on the roster. The directory name is the preset id; the composition file
/// makes it a preset. A preset whose composition is missing or unloadable is reported broken
/// rather than dropped — the directory still occupies its id, and hiding it would leave nothing
/// to see or delete (port of the TS discovery health verdict).
/// </summary>
/// <param name="Id">Stable identifier; the preset directory's name.</param>
/// <param name="CompositionPath">Absolute path of the preset's <c>agent.cordis.yml</c> composition.</param>
/// <param name="Broken">Why this preset cannot compose a session; absent when it can.</param>
public sealed record PresetInfo(string Id, string CompositionPath, string? Broken);

/// <summary>
/// One named preset resolved to its composed loader rows (port of the TS resolved preset plus the
/// Include patch composition). The rows are the entry list a loader mount would start from.
/// </summary>
/// <param name="Id">The preset id.</param>
/// <param name="CompositionPath">Absolute path of the preset's composition file.</param>
/// <param name="Rows">The composed plugin rows, in composition order.</param>
public sealed record ComposedPreset(string Id, string CompositionPath, IReadOnlyList<EntryOptions> Rows);

/// <summary>
/// The agent-preset capability surface (port of packages/preset/agent-presets): discovery of
/// preset <c>cordis.yml</c> files under a root and resolution of a named preset to its composed
/// entry list. Discovery re-reads the root on every call so a preset authored while the process
/// runs is visible immediately, and a preset deleted underneath a consumer disappears from the
/// next read.
/// </summary>
public interface IPresetService
{
    /// <summary>
    /// Every preset the root currently supplies, ordered by id. An absent root yields no presets
    /// rather than throwing. A directory whose composition is missing or unloadable is reported
    /// as a broken row; a directory whose name no copy could ever claim is skipped instead.
    /// </summary>
    IReadOnlyList<PresetInfo> Discover();

    /// <summary>
    /// Resolve one preset by id to its composed entry list.
    /// </summary>
    /// <param name="id">The preset id (the directory name).</param>
    /// <returns>The composed preset.</returns>
    /// <exception cref="InvalidOperationException">when no preset with that id exists, or its
    /// composition is missing, unparsable, or not an entry list.</exception>
    ComposedPreset Resolve(string id);
}
