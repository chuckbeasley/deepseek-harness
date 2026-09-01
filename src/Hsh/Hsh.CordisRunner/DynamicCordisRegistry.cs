using Harness.Session;

namespace Harness.CordisRunner;

/// <summary>One immutable package version stored on a dynamic plugin.</summary>
public sealed record DynamicCordisDefinition(
    string PackageId,
    string Name,
    string Purpose,
    string? HostCode,
    string? ClientCode)
{
    public bool HasHostHalf => HostCode is not null;

    public bool HasClientHalf => ClientCode is not null;
}

/// <summary>One live activation of a dynamic plugin (the port keeps the host fiber minimal: the recorded host status only).</summary>
public sealed record DynamicCordisRun(
    string PluginRunId,
    string PackageId);

/// <summary>One activation attempt: the latest run plus its status ledger.</summary>
public sealed record DynamicCordisRunAttempt(
    string PluginRunId,
    string PackageId,
    string Mode,
    string Status,
    string HostStatus,
    IReadOnlyList<string> HostWaitingFor,
    string ClientStatus,
    IReadOnlyList<string> ClientWaitingFor);

/// <summary>One stable dynamic plugin owned by a session.</summary>
public sealed class DynamicCordisPlugin
{
    public required string PluginId { get; init; }

    public required SessionId SessionId { get; init; }

    public Dictionary<string, DynamicCordisDefinition> Packages { get; } = new(StringComparer.Ordinal);

    public string? CurrentPackageId { get; set; }

    public string? NextPackageId { get; set; }

    public DynamicCordisRun? Run { get; set; }

    public DynamicCordisRunAttempt? LatestRun { get; set; }
}

/// <summary>
/// The dynamic Cordis package registry and its opaque identity mints (port of the vendored
/// <c>DynamicCordisRegistry</c>): plugins keyed by stable id, packages in define order per plugin,
/// and process-wide counters for plugin/package/run identities.
/// </summary>
public sealed class DynamicCordisRegistry
{
    private readonly Dictionary<string, DynamicCordisPlugin> _plugins = new(StringComparer.Ordinal);
    private int _nextPlugin = 1;
    private int _nextPackage = 1;
    private int _nextRun = 1;

    /// <summary>Mint a semantic plugin id without reusing a prior suffix.</summary>
    public string MintPluginId(string prefix)
    {
        string id;
        do
        {
            id = $"{prefix}-{_nextPlugin++}";
        }
        while (_plugins.ContainsKey(id));
        return id;
    }

    /// <summary>Mint an immutable package id.</summary>
    public string MintPackageId() => $"pkg-{_nextPackage++}";

    /// <summary>Mint an activation id.</summary>
    public string MintPluginRunId() => $"run-{_nextRun++}";

    /// <summary>Add one stable plugin.</summary>
    public void Add(DynamicCordisPlugin plugin) => _plugins[plugin.PluginId] = plugin;

    /// <summary>Read one plugin, or <c>null</c> when absent.</summary>
    public DynamicCordisPlugin? Get(string pluginId) => _plugins.GetValueOrDefault(pluginId);

    /// <summary>Delete one plugin and all package versions; returns whether a record was removed.</summary>
    public bool Delete(string pluginId) => _plugins.Remove(pluginId);

    /// <summary>Read all plugins in creation order.</summary>
    public IReadOnlyList<DynamicCordisPlugin> All() => _plugins.Values.ToArray();

    /// <summary>Read one session's plugins in creation order.</summary>
    public IReadOnlyList<DynamicCordisPlugin> OfSession(SessionId sessionId)
        => _plugins.Values.Where(plugin => plugin.SessionId == sessionId).ToArray();
}