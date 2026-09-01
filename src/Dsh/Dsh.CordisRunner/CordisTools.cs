using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.CordisRunner;

/// <summary>
/// The model-facing dynamic Cordis tools (port of <c>tool-cordis</c>): define, run, stop,
/// undefine, inspect-list, inspect-query, and layered self inspection over the session-owned
/// plugin registry. The rendered text and the execute values match the recorded fixtures
/// byte-exact (the tool schemas are tokenized as <c>{{tools}}</c> by the corpus normalizer, so
/// they stay close but not verbatim).
/// </summary>
public static class CordisTools
{
    /// <summary>
    /// The seven tool definitions, all bound to one runner, and the host inspect providers
    /// registered on the runner's registry.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Definitions(DynamicCordisRunner runner, ToolRuntime? tools = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        foreach (var provider in CordisInspectProviders.Build(tools))
        {
            runner.Inspect.Register(provider);
        }
        return new[]
        {
            InspectList(runner),
            InspectQuery(runner),
            Define(runner),
            Run(runner),
            Stop(runner),
            Undefine(runner),
            InspectSelf(runner),
        };
    }

    private static ToolDefinition InspectList(DynamicCordisRunner runner) => new(
        Name: "cordis_inspect_list",
        Description: "List every Cordis Inspect Provider currently known to the Host, including local Host Providers and the latest "
            + "manifests synchronized from the Client. Each entry includes its platform, purpose, read-only methods, and "
            + "input/output schemas. Call this Tool before creating or modifying a Package, then select the provider and "
            + "method for cordis_inspect_query from its result. Do not guess names or treat an Inspect method as a business "
            + "Service that Plugin code can call.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse("{}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"json\"}")!),
        Execute: (_, context) =>
        {
            var providers = new JsonArray();
            foreach (var view in runner.Inspect.List())
            {
                var methods = new JsonArray();
                foreach (var method in view.Methods)
                {
                    methods.Add(new JsonObject
                    {
                        ["name"] = method.Name,
                        ["description"] = method.Description,
                        ["inputSchema"] = JsonNode.Parse(method.InputSchema.GetRawText()),
                        ["outputSchema"] = JsonNode.Parse(method.OutputSchema.GetRawText()),
                    });
                }
                providers.Add(new JsonObject
                {
                    ["platform"] = view.Platform,
                    ["id"] = view.Id,
                    ["description"] = view.Description,
                    ["methods"] = methods,
                });
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new JsonObject { ["providers"] = providers }));
        },
        Render: (_, value) => new ContentBlock[] { new TextBlock(PrettyJsonRelaxed(value)) },
        PersistMeta: false);

    private static ToolDefinition InspectQuery(DynamicCordisRunner runner) => new(
        Name: "cordis_inspect_query",
        Description: "Run a read-only query explicitly declared by an Inspect Provider. platform, provider, and method must come "
            + "from cordis_inspect_list, and input must satisfy that method's schema. Use this Tool before cordis_define "
            + "to read exact Service methods, Event modes, Builtin signatures, or Tool schemas. Host queries run locally; "
            + "the ported version has no Client runtime, so a client query settles with the unregistered-provider refusal. "
            + "This Tool cannot invoke business Service methods or modify the runtime. For Service.listService and "
            + "Event.listEvents, query without input to navigate the compact signature directory, then query the exact "
            + "service or event for its structured contract and referenced types.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":{\"type\":\"string\",\"required\":true,\"enum\":[\"host\",\"client\"],\"description\":\"Runtime platform that owns the Provider.\"}," +
            "\"provider\":{\"type\":\"string\",\"required\":true,\"description\":\"Exact Provider ID returned by cordis_inspect_list.\"}," +
            "\"method\":{\"type\":\"string\",\"required\":true,\"description\":\"Exact method name declared by the Provider manifest.\"}," +
            "\"input\":{\"type\":\"json\",\"description\":\"Optional query input; it must satisfy the method input schema.\"}}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"json\"}")!),
        Execute: (args, context) =>
        {
            var platform = args.GetProperty("platform").GetString() ?? string.Empty;
            var provider = args.GetProperty("provider").GetString() ?? string.Empty;
            var method = args.GetProperty("method").GetString() ?? string.Empty;
            JsonElement? input = args.TryGetProperty("input", out var inputValue) ? inputValue : null;
            var data = runner.Inspect.Query(platform, provider, method, input);
            return Task.FromResult(JsonSerializer.SerializeToElement(new JsonObject
            {
                ["platform"] = platform,
                ["provider"] = provider,
                ["method"] = method,
                ["data"] = JsonNode.Parse(data.GetRawText()),
            }));
        },
        Render: (_, value) => new ContentBlock[] { new TextBlock(PrettyJsonRelaxed(value)) },
        PersistMeta: false);

    private static ToolDefinition Define(DynamicCordisRunner runner) => new(
        Name: "cordis_define",
        Description: "Define an immutable Cordis Package. For a new Plugin, use kind:\"new\" and provide only a semantic prefix of "
            + "3–6 lowercase English letters; the Host returns the final pluginId and packageId. To modify an existing "
            + "Plugin, use kind:\"existing\" with its exact pluginId to append a Package without overwriting older versions. "
            + "Provide at least one of code.host and code.client. Each value is a plain JavaScript function body that returns "
            + "a Cordis Plugin; no TypeScript, JSX, or import transformation occurs. Define only validates parameters and syntax "
            + "and records source: it does not request approval, execute apply, or change currentPackageId. On success, call "
            + "cordis_run with the returned IDs.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plugin\":{\"required\":true,\"oneOf\":[" +
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"kind\":{\"type\":\"string\",\"const\":\"new\",\"required\":true},\"idPrefix\":{\"type\":\"string\",\"required\":true,\"description\":\"Suggested semantic prefix of 3–6 lowercase English letters; the Host adds a unique numeric suffix.\"}}}," +
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"kind\":{\"type\":\"string\",\"const\":\"existing\",\"required\":true},\"pluginId\":{\"type\":\"string\",\"required\":true,\"description\":\"Exact ID of an existing Plugin; the new Package is appended to that instance.\"}}}]}," +
            "\"name\":{\"type\":\"string\",\"required\":true,\"description\":\"Short, readable Package name.\"}," +
            "\"purpose\":{\"type\":\"string\",\"required\":true,\"description\":\"One-sentence, user-facing description of the Package purpose.\"}," +
            "\"code\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{" +
            "\"host\":{\"type\":\"string\",\"description\":\"Plain JavaScript function body that returns the Host-half Cordis Plugin.\"}," +
            "\"client\":{\"type\":\"string\",\"description\":\"Plain JavaScript function body that returns the browser Client-half Cordis Plugin.\"}}}}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" +
            "\"pluginId\":{\"type\":\"string\",\"required\":true},\"packageId\":{\"type\":\"string\",\"required\":true}," +
            "\"name\":{\"type\":\"string\",\"required\":true},\"purpose\":{\"type\":\"string\",\"required\":true}," +
            "\"hasHostHalf\":{\"type\":\"boolean\",\"required\":true},\"hasClientHalf\":{\"type\":\"boolean\",\"required\":true}}}")!),
        Execute: (args, context) => Task.FromResult(DefineExecute(runner, args, context)),
        Render: (_, value) => new ContentBlock[] { new TextBlock(
            $"Defined {value.GetProperty("pluginId").GetString()}/{value.GetProperty("packageId").GetString()} "
            + $"({value.GetProperty("name").GetString()}); it is not running yet. Use cordis_run to activate this Package.") },
        MetaOf: value => JsonSerializer.SerializeToElement(new JsonObject
        {
            ["pluginId"] = value.GetProperty("pluginId").GetString(),
            ["packageId"] = value.GetProperty("packageId").GetString(),
        }));

    private static JsonElement DefineExecute(DynamicCordisRunner runner, JsonElement args, ToolRunContext context)
    {
        var plugin = args.GetProperty("plugin");
        var kind = plugin.GetProperty("kind").GetString() ?? string.Empty;
        var idPrefix = plugin.TryGetProperty("idPrefix", out var prefix) ? prefix.GetString() : null;
        var existing = plugin.TryGetProperty("pluginId", out var existingId) ? existingId.GetString() : null;
        var code = args.GetProperty("code");
        var host = code.TryGetProperty("host", out var hostValue) ? hostValue.GetString() : null;
        var client = code.TryGetProperty("client", out var clientValue) ? clientValue.GetString() : null;
        var receipt = runner.Define(
            RequireSession(context).Id,
            kind, idPrefix, existing,
            args.GetProperty("name").GetString() ?? string.Empty,
            args.GetProperty("purpose").GetString() ?? string.Empty,
            host, client);
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["pluginId"] = receipt.PluginId,
            ["packageId"] = receipt.PackageId,
            ["name"] = receipt.Name,
            ["purpose"] = receipt.Purpose,
            ["hasHostHalf"] = receipt.HasHostHalf,
            ["hasClientHalf"] = receipt.HasClientHalf,
        });
    }

    private static ToolDefinition Run(DynamicCordisRunner runner) => new(
        Name: "cordis_run",
        Description: "Activate one exact Package of a dynamic Plugin. Use mode:\"run\" for the first activation, restarting "
            + "currentPackageId, or rollback. When current exists, use mode:\"update\" to switch to a different Package, "
            + "even if the Plugin is currently stopped. The ported version runs host-only packages.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":{\"type\":\"string\",\"required\":true,\"description\":\"Stable Plugin ID returned by cordis_define.\"}," +
            "\"packageId\":{\"type\":\"string\",\"required\":true,\"description\":\"Exact immutable Package ID to activate under that Plugin.\"}," +
            "\"mode\":{\"type\":\"string\",\"required\":true,\"enum\":[\"run\",\"update\"],\"description\":\"Use run for the first activation, restarting current, or rollback; use update to switch from current to a different Package.\"}}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"json\"}")!),
        Execute: (args, context) => RunExecute(runner, args, context),
        Render: (_, value) => new ContentBlock[] { new TextBlock(RunRender(value)) },
        MetaOf: value => JsonSerializer.SerializeToElement(new JsonObject
        {
            ["pluginId"] = value.GetProperty("pluginId").GetString(),
            ["packageId"] = value.GetProperty("packageId").GetString(),
            ["pluginRunId"] = value.GetProperty("pluginRunId").GetString(),
        }));

    private static async Task<JsonElement> RunExecute(DynamicCordisRunner runner, JsonElement args, ToolRunContext context)
    {
        var response = await runner.RunAsync(
            RequireSession(context).Id,
            args.GetProperty("pluginId").GetString() ?? string.Empty,
            args.GetProperty("packageId").GetString() ?? string.Empty,
            args.GetProperty("mode").GetString() ?? string.Empty,
            context.CancellationToken).ConfigureAwait(false);
        var json = new JsonObject
        {
            ["status"] = response.Status,
            ["pluginId"] = response.PluginId,
            ["packageId"] = response.PackageId,
            ["pluginRunId"] = response.PluginRunId,
            ["currentPackageId"] = response.CurrentPackageId,
            ["host"] = new JsonObject
            {
                ["status"] = response.HostStatus,
                ["provides"] = new JsonArray(response.Provides.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                ["waitingFor"] = new JsonArray(response.HostWaitingFor.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            },
            ["client"] = new JsonObject
            {
                ["status"] = response.ClientStatus,
                ["waitingFor"] = new JsonArray(response.ClientWaitingFor.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            },
        };
        return JsonSerializer.SerializeToElement(json);
    }

    private static string RunRender(JsonElement value)
    {
        var status = value.GetProperty("status").GetString();
        var pluginId = value.GetProperty("pluginId").GetString();
        var packageId = value.GetProperty("packageId").GetString();
        var pluginRunId = value.GetProperty("pluginRunId").GetString();
        return status == "awaiting-approval"
            ? $"{pluginId}/{packageId} is awaiting user approval ({pluginRunId})."
            : status == "starting"
                ? $"{pluginId}/{packageId} is starting asynchronously ({pluginRunId})."
                : $"{pluginId}/{packageId} is running ({pluginRunId}).";
    }

    private static ToolDefinition Stop(DynamicCordisRunner runner) => new(
        Name: "cordis_stop",
        Description: "Stop the current Run of a dynamic Plugin. Retain the Plugin, every immutable Package, grants, "
            + "currentPackageId, and nextPackageId so it can later run or update directly. Stopping an already stopped "
            + "Plugin succeeds idempotently.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":{\"type\":\"string\",\"required\":true,\"description\":\"Stable dynamic Plugin ID to stop.\"}}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"pluginId\":{\"type\":\"string\",\"required\":true}}}")!),
        Execute: (args, context) =>
        {
            var result = runner.Stop(RequireSession(context).Id, args.GetProperty("pluginId").GetString() ?? string.Empty);
            if (!result.Ok && result.Reason != "not-running") throw new InvalidOperationException(result.Message);
            return Task.FromResult(JsonSerializer.SerializeToElement(new JsonObject
            {
                ["pluginId"] = args.GetProperty("pluginId").GetString(),
            }));
        },
        Render: (_, value) => new ContentBlock[] { new TextBlock($"Dynamic Plugin {value.GetProperty("pluginId").GetString()} is stopped; its definition and versions remain.") },
        PersistMeta: false);

    private static ToolDefinition Undefine(DynamicCordisRunner runner) => new(
        Name: "cordis_undefine",
        Description: "Permanently remove a dynamic Plugin owned by the current Session. If it is running or awaiting approval, "
            + "first stop it and cancel the request, then delete every Package, grant, and version pointer.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":{\"type\":\"string\",\"required\":true,\"description\":\"Stable dynamic Plugin ID to remove permanently.\"}}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" +
            "\"pluginId\":{\"type\":\"string\",\"required\":true},\"wasRunning\":{\"type\":\"boolean\",\"required\":true}}}")!),
        Execute: (args, context) =>
        {
            var pluginId = args.GetProperty("pluginId").GetString() ?? string.Empty;
            var result = runner.Undefine(RequireSession(context).Id, pluginId);
            if (!result.Ok) throw new InvalidOperationException(result.Message);
            return Task.FromResult(JsonSerializer.SerializeToElement(new JsonObject
            {
                ["pluginId"] = pluginId,
                ["wasRunning"] = result.WasRunning,
            }));
        },
        Render: (_, value) => new ContentBlock[] { new TextBlock($"Removed dynamic Plugin {value.GetProperty("pluginId").GetString()} and all of its Packages.") },
        PersistMeta: false);

    private static ToolDefinition InspectSelf(DynamicCordisRunner runner) => new(
        Name: "cordis_inspect_self",
        Description: "Inspect dynamic Cordis objects owned by the current Session at increasing levels of detail. With no IDs, "
            + "list only Plugin summaries. With pluginId alone, return version pointers, the latest Run, and every Package "
            + "summary. Only pluginId plus packageId returns that immutable Package's Host/Client source and runtime "
            + "diagnostics. packageId cannot be supplied alone. This Tool is read-only.",
        Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":{\"type\":\"string\",\"description\":\"Stable Plugin ID returned by cordis_define or injected by @pluginId; omit it to list every current Plugin.\"}," +
            "\"packageId\":{\"type\":\"string\",\"description\":\"Exact immutable Package ID owned by pluginId; when specified, source and diagnostics are returned.\"}}")!),
        OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"json\"}")!),
        Execute: (args, context) =>
        {
            var session = RequireSession(context);
            var pluginId = args.TryGetProperty("pluginId", out var pluginValue) ? pluginValue.GetString() : null;
            var packageId = args.TryGetProperty("packageId", out var packageValue) ? packageValue.GetString() : null;
            if (packageId is not null && pluginId is null)
            {
                throw new InvalidOperationException("cordis_inspect_self packageId requires pluginId");
            }
            JsonObject value;
            if (pluginId is null)
            {
                var plugins = new JsonArray();
                foreach (var plugin in runner.ListPlugins(session.Id))
                {
                    plugins.Add(SelfSummary(plugin));
                }
                value = new JsonObject { ["mode"] = "plugins", ["plugins"] = plugins };
            }
            else if (packageId is null)
            {
                var plugin = runner.InspectPlugin(session.Id, pluginId);
                value = new JsonObject { ["mode"] = "plugin" };
                FillSelfSummary(value, plugin);
                value["packages"] = new JsonArray(plugin!.Packages.Select(item => (JsonNode?)item).ToArray());
            }
            else
            {
                value = InspectSelfPackage(runner, session.Id, pluginId, packageId);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(value));
        },
        Render: (_, value) => new ContentBlock[] { new TextBlock(PrettyJson(value)) },
        PersistMeta: false);

    /// <summary>The <c>mode:"plugin"</c> summary: version pointers, latest run, and package summaries (fixture key order).</summary>
    private static JsonObject SelfSummary(DynamicCordisPluginInspection plugin)
    {
        var summary = new JsonObject();
        FillSelfSummary(summary, plugin);
        return summary;
    }

    /// <summary>Fill the plugin summary entries into an existing object (fresh values, so the caller may add them to another parent).</summary>
    private static void FillSelfSummary(JsonObject target, DynamicCordisPluginInspection? plugin)
    {
        if (plugin is null) return;
        target["pluginId"] = plugin.PluginId;
        target["name"] = plugin.Name;
        target["packageCount"] = plugin.Packages.Count;
        target["state"] = SelfState(plugin);
        if (plugin.CurrentPackageId is not null) target["currentPackageId"] = plugin.CurrentPackageId;
        if (plugin.NextPackageId is not null) target["nextPackageId"] = plugin.NextPackageId;
        if (plugin.ActiveRun is { } active)
        {
            target["activeRun"] = new JsonObject
            {
                ["pluginRunId"] = active.PluginRunId,
                ["packageId"] = active.PackageId,
            };
        }
    }

    /// <summary>The lifecycle state label: the latest run's status when one exists, else defined/stopped.</summary>
    private static string SelfState(DynamicCordisPluginInspection plugin)
    {
        return plugin.ActiveRun is not null ? "running" : plugin.CurrentPackageId is null ? "defined" : "stopped";
    }

    /// <summary>The <c>mode:"package"</c> inspection: metadata, source, and the lifecycle pointers.</summary>
    private static JsonObject InspectSelfPackage(DynamicCordisRunner runner, SessionId sessionId, string pluginId, string packageId)
    {
        var plugin = runner.InspectPlugin(sessionId, pluginId) ?? throw new InvalidOperationException($"no dynamic plugin \"{pluginId}\" in this process");
        var packages = plugin.Packages.ToArray();
        var pkg = packages.FirstOrDefault(item => item["packageId"]?.GetValue<string>() == packageId)
            ?? throw new InvalidOperationException($"dynamic package \"{packageId}\" does not exist on plugin \"{pluginId}\"");
        var code = new JsonObject();
        if (pkg["hasHostHalf"]?.GetValue<bool>() == true) code["host"] = HostSource(pkg);
        if (pkg["hasClientHalf"]?.GetValue<bool>() == true) code["client"] = ClientSource(pkg);
        var value = new JsonObject
        {
            ["mode"] = "package",
            ["plugin"] = SelfSummary(plugin),
            ["packageId"] = packageId,
            ["name"] = pkg["name"]?.GetValue<string>(),
            ["purpose"] = pkg["purpose"]?.GetValue<string>(),
            ["code"] = code,
            ["runtime"] = new JsonObject
            {
                ["state"] = SelfState(plugin),
                ["host"] = new JsonObject
                {
                    ["status"] = pkg["hasHostHalf"]?.GetValue<bool>() == true ? "running" : "absent",
                    ["provides"] = new JsonArray(),
                    ["waitingFor"] = new JsonArray(),
                    ["handlers"] = new JsonArray(),
                },
                ["client"] = new JsonObject
                {
                    ["status"] = pkg["hasClientHalf"]?.GetValue<bool>() == true ? "running" : "absent",
                    ["waitingFor"] = new JsonArray(),
                },
            },
        };
        return value;
    }

    private static string HostSource(JsonObject pkg) => pkg.TryGetPropertyValue("hostSource", out var source) ? source!.GetValue<string>() : string.Empty;

    private static string ClientSource(JsonObject pkg) => pkg.TryGetPropertyValue("clientSource", out var source) ? source!.GetValue<string>() : string.Empty;

    private static Dsh.Session.Session RequireSession(ToolRunContext context)
        => context.Session ?? throw new InvalidOperationException("the cordis tools require a calling session");

    /// <summary>Two-space LF pretty JSON, matching the recorded render.</summary>
    private static string PrettyJson(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            value.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Two-space LF pretty JSON with the relaxed encoder: the inspect-list/query renderers must
    /// reproduce the recorded <c>JSON.stringify(value, null, 2)</c> bytes, which do not escape
    /// the <c>&lt;</c>/<c>&gt;</c>/<c>&amp;</c>/<c>'</c> characters the default encoder would.
    /// </summary>
    private static string PrettyJsonRelaxed(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            value.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}