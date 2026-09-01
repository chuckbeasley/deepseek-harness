using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Llm;
using Harness.Tools;

namespace Harness.CordisRunner;

/// <summary>
/// The first-party Host inspect providers (port of <c>hostInspectProviders</c>): the static
/// catalog queries (Service/Event), the sandbox builtin directory (Builtin), and the live tool
/// schemas (Tool). The providers register on the runner's <see cref="CordisInspect"/> registry;
/// a <c>null</c> tool runtime answers an empty tool directory (the registry is always mounted
/// over the base bundle's tools row).
/// </summary>
public static class CordisInspectProviders
{
    private static readonly JsonElement EmptyInput = Schema(
        "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");
    private static readonly JsonElement AnyOutput = Schema(
        "{\"description\":\"JSON data owned by this inspect provider.\"}");
    private static readonly JsonElement ServiceInput = Schema(
        "{\"type\":\"object\",\"properties\":{\"service\":{\"type\":\"string\",\"description\":\"Exact Service key. Omit it for the compact Service and method-signature directory.\"}},\"additionalProperties\":false}");
    private static readonly JsonElement EventInput = Schema(
        "{\"type\":\"object\",\"properties\":{\"event\":{\"type\":\"string\",\"description\":\"Exact Event name. Omit it for the compact Event and listener-signature directory.\"}},\"additionalProperties\":false}");
    private static readonly JsonElement ServiceOutput = Schema(
        "{\"description\":\"Compact Service directory, or one exact Service contract with only its referenced type declarations.\"}");
    private static readonly JsonElement EventOutput = Schema(
        "{\"description\":\"Compact Event directory, or one exact Event contract with only its referenced type declarations.\"}");

    /// <summary>Build the four host providers over the runner's inspect registry.</summary>
    public static IReadOnlyList<CordisInspectProvider> Build(ToolRuntime? tools)
    {
        return new[]
        {
            new CordisInspectProvider
            {
                Manifest = new CordisInspectProviderManifest(
                    "Service",
                    "Progressive Host Service discovery: compact capability/signature directory, then one exact coding contract.",
                    new[] { new CordisInspectMethod("listService",
                        "Progressive Host Service discovery: compact capability/signature directory, then one exact coding contract.",
                        ServiceInput, ServiceOutput) }),
                Query = (_, input) => JsonSerializer.SerializeToElement(CordisApiCatalog.QueryService(input)),
            },
            new CordisInspectProvider
            {
                Manifest = new CordisInspectProviderManifest(
                    "Event",
                    "Progressive Host Event discovery: compact listener directory, then one exact event contract.",
                    new[] { new CordisInspectMethod("listEvents",
                        "Progressive Host Event discovery: compact listener directory, then one exact event contract.",
                        EventInput, EventOutput) }),
                Query = (_, input) => JsonSerializer.SerializeToElement(CordisApiCatalog.QueryEvents(input)),
            },
            new CordisInspectProvider
            {
                Manifest = new CordisInspectProviderManifest(
                    "Builtin",
                    "Plain-JavaScript symbols available to a dynamic Host half.",
                    new[] { new CordisInspectMethod("listBuiltins",
                        "Plain-JavaScript symbols available to a dynamic Host half.",
                        EmptyInput, AnyOutput) }),
                Query = (_, _) => JsonSerializer.SerializeToElement(CordisApiCatalog.ListBuiltins()),
            },
            new CordisInspectProvider
            {
                Manifest = new CordisInspectProviderManifest(
                    "Tool",
                    "Tools visible to the requesting Agent, including scoped and dynamic registrations.",
                    new[] { new CordisInspectMethod("listTools",
                        "Return every Tool schema currently callable by this Agent.",
                        EmptyInput, AnyOutput) }),
                Query = (_, _) => JsonSerializer.SerializeToElement(
                    CordisApiCatalog.ListTools(tools?.Schemas() ?? Array.Empty<ToolSchema>())),
            },
        };
    }

    private static JsonElement Schema(string json) => JsonSerializer.SerializeToElement(JsonNode.Parse(json)!);
}