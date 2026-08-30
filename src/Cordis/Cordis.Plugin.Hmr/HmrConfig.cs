namespace Cordis.Plugin.Hmr;

/// <summary>
/// Config for the HMR service (port of the vendored <c>Hmr.Config</c> minus the main module
/// watcher fields <c>root</c> and <c>ignored</c>). The vendored service watches module roots and
/// reloads ESM dependency trees; the port watches exact config files only, because the loader
/// imports plugin types from a catalog instead of ESM modules, so there is no module tree to
/// reload (documented deviation). <see cref="Base"/> and <see cref="DebounceMs"/> keep their
/// vendored defaults.
/// </summary>
public sealed class HmrConfig
{
    /// <summary>
    /// Base directory that relative config paths resolve against. The vendored default resolves
    /// <c>'.'</c> against the context base URL; the port has no base URL, so the default is the
    /// current working directory.
    /// </summary>
    public string Base { get; set; } = ".";

    /// <summary>
    /// Debounce interval in milliseconds applied to each config registration: change events are
    /// coalesced within one interval before a refresh runs, and events arriving during a refresh
    /// schedule another one. Vendored default: 100.
    /// </summary>
    public int DebounceMs { get; set; } = 100;
}
