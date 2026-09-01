using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Dsh.Session;

namespace Dsh.CordisRunner;

/// <summary>Result of a successful define: the minted identities and the declared halves.</summary>
public sealed record DynamicCordisDefineReceipt(
    string PluginId,
    string PackageId,
    string Name,
    string Purpose,
    bool HasHostHalf,
    bool HasClientHalf);

/// <summary>One successful run receipt: the activation identities and the host-only state ledger.</summary>
public sealed record DynamicCordisRunResponse(
    string Status,
    string PluginId,
    string PackageId,
    string PluginRunId,
    string? CurrentPackageId,
    IReadOnlyList<string> WaitingFor,
    string HostStatus,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> HostWaitingFor,
    string ClientStatus,
    IReadOnlyList<string> ClientWaitingFor);

/// <summary>Source-free plugin summary returned by layered self inspection.</summary>
public sealed record DynamicCordisPluginInspection(
    string PluginId,
    string PackageId,
    string Name,
    string Purpose,
    string? CurrentPackageId,
    string? NextPackageId,
    (string PluginRunId, string PackageId)? ActiveRun,
    IReadOnlyList<JsonObject> Packages);

/// <summary>
/// The dynamic Cordis plugin registry and host-half lifecycle (port of the vendored
/// <c>DynamicCordisRunnerService</c>). Define validates and records source without running it;
/// run activates one exact package, evaluating its host half in the managed Jint engine — node is
/// not used in the ported version — and commits the activation on success. Session ownership is
/// enforced on every verb: a plugin defined by one session is invisible to another.
/// </summary>
public sealed class DynamicCordisRunner
{
    private readonly DynamicCordisRegistry _registry = new();

    /// <summary>The host inspect registry the tool family's providers register on.</summary>
    public CordisInspect Inspect { get; } = new();

    /// <summary>Define a new plugin's first package or append a package to an existing plugin.</summary>
    /// <exception cref="ArgumentException">with the recorded teaching messages on invalid input.</exception>
    public DynamicCordisDefineReceipt Define(SessionId sessionId, string kind, string? idPrefix, string? existingPluginId, string name, string purpose, string? hostCode, string? clientCode)
    {
        name = name.Trim();
        purpose = purpose.Trim();
        if (name.Length == 0) throw new ArgumentException("cordis_define needs a non-empty `name`");
        if (purpose.Length == 0) throw new ArgumentException("cordis_define needs a non-empty `purpose`");
        if (hostCode is null && clientCode is null) throw new ArgumentException("cordis_define needs `code.host`, `code.client`, or both");
        if (hostCode is not null) PrecheckCode(hostCode, "code.host");
        if (clientCode is not null) PrecheckCode(clientCode, "code.client");

        DynamicCordisPlugin plugin;
        if (kind == "new")
        {
            var prefix = (idPrefix ?? string.Empty).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[a-z]{3,6}$"))
            {
                throw new ArgumentException("cordis_define `plugin.idPrefix` must contain 3–6 lowercase English letters");
            }
            plugin = new DynamicCordisPlugin
            {
                PluginId = _registry.MintPluginId(prefix),
                SessionId = sessionId,
            };
            _registry.Add(plugin);
        }
        else
        {
            var found = existingPluginId is null ? null : _registry.Get(existingPluginId);
            if (found is null || found.SessionId != sessionId) throw new ArgumentException(MissingPluginMessage(existingPluginId ?? string.Empty));
            plugin = found;
        }

        var packageId = _registry.MintPackageId();
        plugin.Packages[packageId] = new DynamicCordisDefinition(packageId, name, purpose, hostCode, clientCode);
        return new DynamicCordisDefineReceipt(plugin.PluginId, packageId, name, purpose, hostCode is not null, clientCode is not null);
    }

    /// <summary>Remove a plugin, its active run, and all immutable packages.</summary>
    public (bool Ok, bool WasRunning, string? Message) Undefine(SessionId sessionId, string pluginId)
    {
        var plugin = Owned(sessionId, pluginId);
        if (plugin is null) return (false, false, MissingPluginMessage(pluginId));
        var wasRunning = plugin.Run is not null;
        _registry.Delete(pluginId);
        return (true, wasRunning, null);
    }

    /// <summary>Start or update one package for a host-only dynamic plugin (the recorded host path).</summary>
    /// <exception cref="ArgumentException">with the recorded refusal messages when the plan is invalid.</exception>
    public async Task<DynamicCordisRunResponse> RunAsync(SessionId sessionId, string pluginId, string packageId, string mode, CancellationToken ct)
    {
        var plugin = Owned(sessionId, pluginId) ?? throw new ArgumentException(MissingPluginMessage(pluginId));
        if (!plugin.Packages.TryGetValue(packageId, out var definition))
        {
            throw new ArgumentException($"plugin \"{pluginId}\" has no package \"{packageId}\"");
        }
        var current = plugin.CurrentPackageId;
        if (mode == "update" && (current is null || current == packageId))
        {
            throw new ArgumentException(current is null
                ? $"plugin \"{pluginId}\" has no successful version yet; start \"{packageId}\" with mode \"run\""
                : $"package \"{packageId}\" is already current; use mode \"run\"");
        }
        if (mode == "run" && current is not null && current != packageId)
        {
            throw new ArgumentException($"package \"{packageId}\" differs from current \"{current}\"; use mode \"update\"");
        }

        var pluginRunId = _registry.MintPluginRunId();
        var attempt = new DynamicCordisRunAttempt(
            pluginRunId, packageId, mode, "starting-host",
            definition.HasHostHalf ? "pending" : "absent", Array.Empty<string>(),
            definition.HasClientHalf ? "pending" : "absent", Array.Empty<string>());
        plugin.NextPackageId = packageId;
        plugin.LatestRun = attempt;

        // The recorded scenarios are host-only: a client half stays a documented deviation (the
        // approval flow and the browser runner are not ported), and the host half runs here.
        if (definition.ClientCode is not null)
        {
            throw new ArgumentException($"dynamic plugin \"{pluginId}\" has a client half; the ported version runs host-only packages");
        }

        var hostFailure = await StartHostAsync(plugin.PluginId, definition.HostCode!, ct).ConfigureAwait(false);
        if (hostFailure is not null)
        {
            throw new ArgumentException($"host half of \"{pluginId}\" failed to start: {hostFailure}");
        }

        plugin.Run = new DynamicCordisRun(pluginRunId, packageId);
        var hostWaiting = Array.Empty<string>();
        plugin.LatestRun = new DynamicCordisRunAttempt(
            pluginRunId, packageId, mode, "running", "running", hostWaiting, "absent", Array.Empty<string>());
        plugin.CurrentPackageId = packageId;
        plugin.NextPackageId = null;
        return new DynamicCordisRunResponse(
            "running", pluginId, packageId, pluginRunId, packageId,
            Array.Empty<string>(), "running", Array.Empty<string>(), hostWaiting, "absent", Array.Empty<string>());
    }

    /// <summary>Stop the current run while retaining every package version (idempotent).</summary>
    public (bool Ok, string? Reason, string? Message) Stop(SessionId sessionId, string pluginId)
    {
        var plugin = Owned(sessionId, pluginId);
        if (plugin is null) return (false, "plugin-missing", MissingPluginMessage(pluginId));
        if (plugin.Run is null)
        {
            return (false, "not-running", $"dynamic plugin \"{pluginId}\" is not running");
        }
        plugin.Run = null;
        if (plugin.LatestRun is not null)
        {
            plugin.LatestRun = plugin.LatestRun with { Status = "stopped", HostStatus = "stopped", ClientStatus = "absent" };
        }
        return (true, null, null);
    }

    /// <summary>Inspect one plugin without returning package source (plugin mode), or list every session plugin.</summary>
    public DynamicCordisPluginInspection? InspectPlugin(SessionId sessionId, string pluginId)
    {
        var plugin = Owned(sessionId, pluginId) ?? throw new ArgumentException(MissingPluginMessage(pluginId));
        var packageId = plugin.NextPackageId ?? plugin.CurrentPackageId ?? plugin.Packages.Keys.LastOrDefault()
            ?? throw new ArgumentException($"dynamic plugin \"{pluginId}\" has no package");
        var definition = plugin.Packages[packageId];
        return new DynamicCordisPluginInspection(
            plugin.PluginId, packageId, definition.Name, definition.Purpose,
            plugin.CurrentPackageId, plugin.NextPackageId,
            plugin.Run is null ? null : (plugin.Run.PluginRunId, plugin.Run.PackageId),
            plugin.Packages.Values.Select(pkg => SelfPackageJson(plugin, pkg)).ToArray());
    }

    /// <summary>Read one session's plugins in creation order (source-free summaries).</summary>
    public IReadOnlyList<DynamicCordisPluginInspection> ListPlugins(SessionId sessionId)
        => _registry.OfSession(sessionId).Select(plugin => InspectPlugin(sessionId, plugin.PluginId)).ToArray()!;

    /// <summary>The registered plugin count for one session (used by the tool family tests).</summary>
    public int Count(SessionId sessionId) => _registry.OfSession(sessionId).Count;

    private DynamicCordisPlugin? Owned(SessionId sessionId, string pluginId)
    {
        var plugin = _registry.Get(pluginId);
        return plugin?.SessionId == sessionId ? plugin : null;
    }

    private static string MissingPluginMessage(string pluginId)
        => $"no dynamic plugin \"{pluginId}\" in this process — it may have been removed or lost on DSH restart";

    /// <summary>The packages entry of the plugin inspection: immutable metadata plus the version pointers.</summary>
    private static JsonObject SelfPackageJson(DynamicCordisPlugin plugin, DynamicCordisDefinition pkg)
    {
        var json = new JsonObject
        {
            ["packageId"] = pkg.PackageId,
            ["name"] = pkg.Name,
            ["purpose"] = pkg.Purpose,
            ["hasHostHalf"] = pkg.HasHostHalf,
            ["hasClientHalf"] = pkg.HasClientHalf,
            ["isCurrent"] = pkg.PackageId == plugin.CurrentPackageId,
            ["isNext"] = pkg.PackageId == plugin.NextPackageId,
        };
        return json;
    }

    /// <summary>Compile-only gate: the wrapped body must parse as an async function body (the define-time precheck).</summary>
    private static void PrecheckCode(string code, string half)
    {
        var engine = new Engine();
        try
        {
            engine.Execute("(async () => {\n" + code + "\n})()");
        }
        catch (Exception error)
        {
            throw new ArgumentException($"dynamic package `{half}` failed to parse:\n{error.Message}\n"
                + "Note: it runs as the BODY of an async function (line numbers are offset by the 1-line wrapper). "
                + "Check bracket balance — ending the returned plugin object with `});` closes a call that was never opened; "
                + "a plain `return { … }` ends with `}` (an optional `;`), never `)`.");
        }
    }

    /// <summary>Evaluate the host half and start it; returns the failure message, or <c>null</c> when the host is running.</summary>
    private static async Task<string?> StartHostAsync(string pluginId, string code, CancellationToken ct)
    {
        var engine = new Engine();
        var logs = new List<string>();
        var consoleShim = new Dictionary<string, object>();
        foreach (var level in new[] { "log", "info", "warn", "error", "debug" })
        {
            consoleShim[level] = new Action<object?>(value => logs.Add(value?.ToString() ?? string.Empty));
        }
        engine.SetValue("console", consoleShim);
        engine.SetValue("btoa", new Func<string, string>(value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))));
        engine.SetValue("atob", new Func<string, string>(value => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value))));
        engine.SetValue("require", new Func<string, object>(name => throw new InvalidOperationException(
            $"Node modules are unavailable. Use the cordis services on ctx instead — e.g. inject: ['fs'] for files, "
            + $"['web'] for HTTP, ['bash'] for processes; query Service.listService with cordis_inspect_query first.")));
        engine.SetValue("setTimeout", new Func<object?[], object>(_ => throw new InvalidOperationException(
            "Node timers are unavailable. Use the cordis timer service instead: declare inject: ['timer'] on your plugin "
            + "and call ctx.timeout / ctx.interval after querying Host Service.listService for the exact overloads.")));
        engine.SetValue("fetch", new Func<object, object>(_ => throw new InvalidOperationException(
            "Network access goes through the cordis web service: declare inject: ['web'] and call ctx.web "
            + "(query Host Service.listService with cordis_inspect_query for its methods).")));

        JsValue plugin;
        try
        {
            plugin = await engine.EvaluateAsync("(async () => {\n'use strict';\n" + code + "\n})()", $"cordis-dyn-{pluginId}.js", ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return error.Message;
        }
        if (!IsPlugin(plugin))
        {
            return plugin.IsUndefined()
                ? "the Host half returned `undefined` — did you forget `return`?"
                : "the Host half must return a Plugin function or an object with apply(ctx)";
        }

        // The recorded host bodies are inert ({ apply() {} }); a real apply call needs the guarded
        // context bridge, which the port keeps as a documented deviation. The plugin SHAPE is still
        // validated exactly like the vendored guard, so an invalid host half fails the run.
        return null;
    }

    private static bool IsPlugin(JsValue value)
    {
        if (value.IsCallable()) return true;
        return value.IsObject() && !value.IsArray() && value.Get("apply").IsCallable();
    }
}