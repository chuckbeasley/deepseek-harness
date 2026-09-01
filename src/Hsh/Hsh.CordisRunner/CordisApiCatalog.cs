using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Llm;

namespace Harness.CordisRunner;

/// <summary>
/// The ported Cordis API catalog (the C# analog of the generated <c>api-catalog.ts</c>): the
/// recorded <c>tools</c> service contract carried as a committed data file
/// (<c>tools-service-catalog.json</c>, extracted from the recorded corpus response) plus the
/// compact directories the host inspect providers answer. Full method contracts ship only for
/// the tools service (the corpus pin); the remaining seam services and the event catalog await
/// entries (documented reduction). Node is not used: the catalog is read by the managed engine
/// and re-serialized with the TS <c>JSON.stringify(value, null, 2)</c> byte contract.
/// </summary>
public static class CordisApiCatalog
{
    private static readonly JsonObject ToolsContract = LoadToolsContract();

    /// <summary>
    /// The Service.listService query (port of <c>queryServiceApi</c>): the compact signature
    /// directory without an input, or one exact service contract with its referenced type
    /// declarations.
    /// </summary>
    public static JsonObject QueryService(JsonElement? input)
    {
        var key = ReadExact(input, "service");
        if (key is null)
        {
            var services = new JsonArray { DirectoryEntry() };
            return new JsonObject { ["mode"] = "catalog", ["services"] = services };
        }
        if (key != "tools")
        {
            throw new InvalidOperationException($"no catalogued Service named \"{key}\"");
        }
        return (JsonObject)ToolsContract.DeepClone();
    }

    /// <summary>
    /// The Event.listEvents query (port of <c>queryEventApi</c>): the compact listener directory
    /// without an input, or one exact event contract. The ported catalog carries no event
    /// entries yet, so the directory is empty and every exact name fails loud.
    /// </summary>
    public static JsonObject QueryEvents(JsonElement? input)
    {
        var name = ReadExact(input, "event");
        if (name is null)
        {
            return new JsonObject { ["mode"] = "catalog", ["events"] = new JsonArray() };
        }
        throw new InvalidOperationException($"no catalogued Event named \"{name}\"");
    }

    /// <summary>
    /// The Builtin.listBuiltins answer: the plain-JavaScript symbols available to a dynamic Host
    /// half in the ported Jint sandbox, with the trap semantics for the withheld Node APIs.
    /// </summary>
    public static JsonObject ListBuiltins()
    {
        var builtins = new JsonArray
        {
            Builtin("console", "Host logging captured for the run (the port buffers console output; the TS writes through to the host terminal, documented reduction).",
                "console.log(...values): void", "console.info(...values): void", "console.warn(...values): void",
                "console.error(...values): void", "console.debug(...values): void"),
            Builtin("btoa", "Encode UTF-8 text as base64.", "btoa(value: string): string"),
            Builtin("atob", "Decode base64 as UTF-8 text.", "atob(value: string): string"),
            Builtin("require", "Node module trap: modules are unavailable in the ported sandbox.", "require(name: string): never"),
            Builtin("setTimeout", "Node timer trap: timers are unavailable in the ported sandbox.", "setTimeout(callback: Function, delay: number, ...args): never"),
            Builtin("fetch", "Network trap: HTTP goes through the cordis web service instead.", "fetch(url: string, init?: object): never"),
        };
        return new JsonObject { ["builtins"] = builtins, ["referencedTypes"] = new JsonArray() };
    }

    /// <summary>The Tool.listTools answer: every tool schema currently callable in the runtime (the global registry; agent-scoped layers are not ported).</summary>
    public static JsonObject ListTools(IReadOnlyList<ToolSchema> schemas)
    {
        var tools = new JsonArray();
        foreach (var schema in schemas)
        {
            tools.Add(new JsonObject
            {
                ["name"] = schema.Name,
                ["description"] = schema.Description,
                ["parameters"] = JsonNode.Parse(schema.Parameters.GetRawText()),
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    /// <summary>The compact directory entry for the tools service (the summary is the contract's first sentence).</summary>
    private static JsonObject DirectoryEntry()
    {
        var service = ToolsContract["service"]!.AsObject();
        var summary = service["description"]!.GetValue<string>().Split(". ")[0] + ".";
        var methods = new JsonArray();
        foreach (var method in service["methods"]!.AsArray())
        {
            methods.Add(new JsonObject { ["signature"] = method!["signature"]!.DeepClone() });
        }
        return new JsonObject
        {
            ["key"] = "tools",
            ["description"] = summary,
            ["methods"] = methods,
        };
    }

    private static JsonObject Builtin(string name, string description, params string[] signatures)
    {
        var list = new JsonArray();
        foreach (var signature in signatures) list.Add(signature);
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["signatures"] = list,
        };
    }

    private static string? ReadExact(JsonElement? input, string field)
    {
        if (input is null || input.Value.ValueKind != JsonValueKind.Object) return null;
        return input.Value.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static JsonObject LoadToolsContract()
    {
        var stream = typeof(CordisApiCatalog).Assembly.GetManifestResourceStream("Harness.CordisRunner.tools-service-catalog.json")
            ?? throw new InvalidOperationException("the tools service catalog resource is missing");
        using var reader = new StreamReader(stream);
        var node = JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("the tools service catalog resource is not JSON");
        return node.AsObject();
    }
}