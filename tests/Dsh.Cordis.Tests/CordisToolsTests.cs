using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Core;
using Dsh.Code;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.CordisRunner.Tests;

/// <summary>
/// The dynamic Cordis tool family over a real ToolRuntime: define/run/inspect/undefine produce the
/// recorded fixture values, renders, and meta; the run_code integration reproduces the recorded
/// advanced-toolchain step-2 program byte-exact.
/// </summary>
public static class CordisToolsTests
{
    public static async Task Define_MintsAndRendersTheReceipt()
    {
        var (tools, runner, session) = CreateHarness();
        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plugin\":{\"kind\":\"new\",\"idPrefix\":\"snap\"},\"name\":\"Snapshot Marker\","
            + "\"purpose\":\"Exercise the dynamic Cordis Package lifecycle in the snapshot.\","
            + "\"code\":{\"host\":\"return { apply() {} }\"}}")!);
        var (value, result) = await RunAsync(tools, "cordis_define", args, session);
        Assert.Equal("snap-1", value.GetProperty("pluginId").GetString(), "the plugin id mints from the prefix");
        Assert.Equal("pkg-1", value.GetProperty("packageId").GetString(), "the package id mints globally");
        Assert.Equal(true, value.GetProperty("hasHostHalf").GetBoolean(), "the host half is declared");
        Assert.Equal(false, value.GetProperty("hasClientHalf").GetBoolean(), "no client half is declared");
        Assert.Equal("Defined snap-1/pkg-1 (Snapshot Marker); it is not running yet. Use cordis_run to activate this Package.",
            ((TextBlock)result.Content[0]).Text, "the define render matches the recorded text");
        Assert.Equal("{\"pluginId\":\"snap-1\",\"packageId\":\"pkg-1\"}", result.Meta?.GetRawText(), "the define meta is the presentation meta");
    }

    public static async Task Run_ReportsTheHostOnlyActivation()
    {
        var (tools, runner, session) = CreateHarness();
        await RunAsync(tools, "cordis_define", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plugin\":{\"kind\":\"new\",\"idPrefix\":\"snap\"},\"name\":\"Snapshot Marker\","
            + "\"purpose\":\"Exercise the dynamic Cordis Package lifecycle in the snapshot.\","
            + "\"code\":{\"host\":\"return { apply() {} }\"}}")!), session);
        var (value, result) = await RunAsync(tools, "cordis_run", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":\"snap-1\",\"packageId\":\"pkg-1\",\"mode\":\"run\"}")!), session);
        Assert.Equal("{\"status\":\"running\",\"pluginId\":\"snap-1\",\"packageId\":\"pkg-1\",\"pluginRunId\":\"run-1\","
            + "\"currentPackageId\":\"pkg-1\",\"host\":{\"status\":\"running\",\"provides\":[],\"waitingFor\":[]},"
            + "\"client\":{\"status\":\"absent\",\"waitingFor\":[]}}",
            value.GetRawText(), "the run value matches the recorded dispatch value byte-exact");
        Assert.Equal("snap-1/pkg-1 is running (run-1).", ((TextBlock)result.Content[0]).Text, "the run render matches the recorded text");
        Assert.Equal("{\"pluginId\":\"snap-1\",\"packageId\":\"pkg-1\",\"pluginRunId\":\"run-1\"}", result.Meta?.GetRawText(), "the run meta is the presentation meta");
    }

    public static async Task InspectSelf_PluginModeMatchesTheRecordedValue()
    {
        var (tools, runner, session) = CreateHarness();
        await RunAsync(tools, "cordis_define", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plugin\":{\"kind\":\"new\",\"idPrefix\":\"snap\"},\"name\":\"Snapshot Marker\","
            + "\"purpose\":\"Exercise the dynamic Cordis Package lifecycle in the snapshot.\","
            + "\"code\":{\"host\":\"return { apply() {} }\"}}")!), session);
        await RunAsync(tools, "cordis_run", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":\"snap-1\",\"packageId\":\"pkg-1\",\"mode\":\"run\"}")!), session);
        var (value, result) = await RunAsync(tools, "cordis_inspect_self", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":\"snap-1\"}")!), session);
        Assert.Equal("{\"mode\":\"plugin\",\"pluginId\":\"snap-1\",\"name\":\"Snapshot Marker\",\"packageCount\":1,"
            + "\"state\":\"running\",\"currentPackageId\":\"pkg-1\",\"activeRun\":{\"pluginRunId\":\"run-1\",\"packageId\":\"pkg-1\"},"
            + "\"packages\":[{\"packageId\":\"pkg-1\",\"name\":\"Snapshot Marker\","
            + "\"purpose\":\"Exercise the dynamic Cordis Package lifecycle in the snapshot.\","
            + "\"hasHostHalf\":true,\"hasClientHalf\":false,\"isCurrent\":true,\"isNext\":false}]}",
            value.GetRawText(), "the inspect value matches the recorded dispatch value byte-exact");
        Assert.Equal(null, result.Meta, "inspect persists no meta");
    }

    public static async Task Undefine_RemovesThePlugin()
    {
        var (tools, runner, session) = CreateHarness();
        await RunAsync(tools, "cordis_define", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plugin\":{\"kind\":\"new\",\"idPrefix\":\"snap\"},\"name\":\"Snapshot Marker\","
            + "\"purpose\":\"Exercise the dynamic Cordis Package lifecycle in the snapshot.\","
            + "\"code\":{\"host\":\"return { apply() {} }\"}}")!), session);
        var (value, result) = await RunAsync(tools, "cordis_undefine", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"pluginId\":\"snap-1\"}")!), session);
        Assert.Equal("{\"pluginId\":\"snap-1\",\"wasRunning\":false}", value.GetRawText(), "the undefine value reports the stopped run");
        Assert.Equal("Removed dynamic Plugin snap-1 and all of its Packages.", ((TextBlock)result.Content[0]).Text, "the undefine render matches the recorded text");
        Assert.Equal(null, result.Meta, "undefine persists no meta");
        Assert.Equal(0, runner.Count(session.Id), "the plugin is gone");
    }

    public static async Task RunCode_IntegrationReproducesTheRecordedStep()
    {
        var (tools, runner, session) = CreateHarness();
        await RunAsync(tools, "cordis_define", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plugin\":{\"kind\":\"new\",\"idPrefix\":\"snap\"},\"name\":\"Snapshot Marker\","
            + "\"purpose\":\"Exercise the dynamic Cordis Package lifecycle in the snapshot.\","
            + "\"code\":{\"host\":\"return { apply() {} }\"}}")!), session);
        var program = "const run = await tools.cordis_run({ pluginId: 'snap-1', packageId: 'pkg-1', mode: 'run' });\n"
            + "const inspected = await tools.cordis_inspect_self({ pluginId: 'snap-1' });\n"
            + "return { run, inspected };";
        var codeTool = RunCodeTool.Definition(tools);
        tools.Register(codeTool);
        var args = JsonSerializer.SerializeToElement(new { code = program, description = "Run and inspect the dynamic Cordis Package" });
        var (value, result) = await RunAsync(tools, "run_code", args, session);
        Assert.Equal("{\n  \"run\": {\n    \"status\": \"running\",\n    \"pluginId\": \"snap-1\",\n    \"packageId\": \"pkg-1\",\n"
            + "    \"pluginRunId\": \"run-1\",\n    \"currentPackageId\": \"pkg-1\",\n    \"host\": {\n      \"status\": \"running\",\n"
            + "      \"provides\": [],\n      \"waitingFor\": []\n    },\n    \"client\": {\n      \"status\": \"absent\",\n"
            + "      \"waitingFor\": []\n    }\n  },\n  \"inspected\": {\n    \"mode\": \"plugin\",\n    \"pluginId\": \"snap-1\",\n"
            + "    \"name\": \"Snapshot Marker\",\n    \"packageCount\": 1,\n    \"state\": \"running\",\n"
            + "    \"currentPackageId\": \"pkg-1\",\n    \"activeRun\": {\n      \"pluginRunId\": \"run-1\",\n      \"packageId\": \"pkg-1\"\n    },\n"
            + "    \"packages\": [\n      {\n        \"packageId\": \"pkg-1\",\n        \"name\": \"Snapshot Marker\",\n"
            + "        \"purpose\": \"Exercise the dynamic Cordis Package lifecycle in the snapshot.\",\n"
            + "        \"hasHostHalf\": true,\n        \"hasClientHalf\": false,\n        \"isCurrent\": true,\n        \"isNext\": false\n      }\n    ]\n  }\n}",
            value.GetString(), "the run_code result reproduces the recorded tool/result text byte-exact");
        var starts = session.Events.OfType<ToolCodeDispatchStartEvent>().ToArray();
        var ends = session.Events.OfType<ToolCodeDispatchEvent>().ToArray();
        Assert.Equal(2, starts.Length, "two dispatch starts");
        Assert.Equal(2, ends.Length, "two dispatch ends");
        Assert.Equal("advanced-code:code:1", ends[0].SubCallId.Replace("call", "advanced-code"), "the sub-call ids use the root call id");
        Assert.Equal("cordis_run", ends[0].Name, "the first dispatch is cordis_run");
        Assert.Equal("cordis_inspect_self", ends[1].Name, "the second dispatch is cordis_inspect_self");
    }

    public static async Task InspectQuery_ToolsContractMatchesTheRecordedFixture()
    {
        var (tools, runner, session) = CreateHarness();
        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Service\",\"method\":\"listService\",\"input\":{\"service\":\"tools\"}}")!);
        var (value, result) = await RunAsync(tools, "cordis_inspect_query", args, session);
        var rendered = ((TextBlock)result.Content[0]).Text;
        Assert.Equal(RecordedInspectQueryText(), rendered, "the tools contract render matches the recorded corpus response byte-exact");
        Assert.Equal(null, result.Meta, "inspect_query persists no meta");
        using var document = JsonDocument.Parse(rendered);
        Assert.Equal("host", document.RootElement.GetProperty("platform").GetString(), "the envelope carries the platform");
        Assert.Equal("Service", document.RootElement.GetProperty("provider").GetString(), "the envelope carries the provider");
        Assert.Equal("listService", document.RootElement.GetProperty("method").GetString(), "the envelope carries the method");
    }

    public static async Task InspectQuery_ServiceDirectoryMode()
    {
        var (tools, runner, session) = CreateHarness();
        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Service\",\"method\":\"listService\"}")!);
        var (value, result) = await RunAsync(tools, "cordis_inspect_query", args, session);
        Assert.Equal("{\n  \"platform\": \"host\",\n  \"provider\": \"Service\",\n  \"method\": \"listService\",\n"
            + "  \"data\": {\n    \"mode\": \"catalog\",\n    \"services\": [\n      {\n        \"key\": \"tools\",\n"
            + "        \"description\": \"Tool registry and execution pipeline.\",\n        \"methods\": [\n"
            + "          {\n            \"signature\": \"presentAs(mode: ToolPresentationMode): () => void\"\n          },\n"
            + "          {\n            \"signature\": \"register(definition: ToolDefinition): () => void\"\n          },\n"
            + "          {\n            \"signature\": \"restrict(filter: ToolRestriction): () => void\"\n          },\n"
            + "          {\n            \"signature\": \"guard(guard: ToolGuard): () => void\"\n          },\n"
            + "          {\n            \"signature\": \"get(name: string, scope?: ScopeKey): ToolDefinition | undefined\"\n          },\n"
            + "          {\n            \"signature\": \"schemas(scope?: ScopeKey): ToolSchema[]\"\n          },\n"
            + "          {\n            \"signature\": \"executionMode(exec: ToolExecutionInput): ToolExecutionMode\"\n          },\n"
            + "          {\n            \"signature\": \"async execute(exec: ToolExecutionInput): Promise<ToolExecutionResult>\"\n          }\n"
            + "        ]\n      }\n    ]\n  }\n}",
            ((TextBlock)result.Content[0]).Text, "the compact service directory matches the TS shape");
    }

    public static async Task InspectList_ListsTheFourHostProviders()
    {
        var (tools, runner, session) = CreateHarness();
        var (value, result) = await RunAsync(tools, "cordis_inspect_list", JsonSerializer.SerializeToElement(new { }), session);
        using var document = JsonDocument.Parse(((TextBlock)result.Content[0]).Text);
        var providers = document.RootElement.GetProperty("providers");
        Assert.Equal(4, providers.GetArrayLength(), "four host providers");
        var ids = providers.EnumerateArray().Select(provider => provider.GetProperty("id").GetString()).ToArray();
        Assert.Equal("Service", ids[0], "the first provider is Service");
        Assert.Equal("Event", ids[1], "the second provider is Event");
        Assert.Equal("Builtin", ids[2], "the third provider is Builtin");
        Assert.Equal("Tool", ids[3], "the fourth provider is Tool");
        Assert.Equal("host", providers[0].GetProperty("platform").GetString(), "host providers carry the host platform");
        Assert.Equal("listService", providers[0].GetProperty("methods")[0].GetProperty("name").GetString(), "the Service method is listService");
        Assert.Equal(null, result.Meta, "inspect_list persists no meta");
    }

    public static async Task InspectQuery_UnknownProviderAndMethodFailLoud()
    {
        var (tools, runner, session) = CreateHarness();
        var missing = await RunFailureAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Nope\",\"method\":\"listService\"}")!), session);
        Assert.Equal("Host Cordis inspect provider \"Nope\" is not registered", missing, "the unknown provider uses the TS vocabulary");
        var method = await RunFailureAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Service\",\"method\":\"listBogus\"}")!), session);
        Assert.Equal("Cordis inspect provider \"Service\" has no method \"listBogus\"", method, "the unknown method uses the TS vocabulary");
        var client = await RunFailureAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"client\",\"provider\":\"Service\",\"method\":\"listService\"}")!), session);
        Assert.Equal("Client Cordis inspect provider \"Service\" is not registered", client, "the client platform has no manifest");
        var unknownService = await RunFailureAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Service\",\"method\":\"listService\",\"input\":{\"service\":\"bogus\"}}")!), session);
        Assert.Equal("no catalogued Service named \"bogus\"", unknownService, "an uncatalogued service fails loud");
        var badInput = await RunFailureAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Service\",\"method\":\"listService\",\"input\":{\"nope\":true}}")!), session);
        Assert.Equal("Host Cordis inspect Service.listService rejected input: input.nope: unknown property", badInput, "input outside the schema is rejected");
    }

    public static async Task InspectQuery_BuiltinAndToolProviders()
    {
        var (tools, runner, session) = CreateHarness();
        var (value, result) = await RunAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Builtin\",\"method\":\"listBuiltins\"}")!), session);
        using var builtins = JsonDocument.Parse(((TextBlock)result.Content[0]).Text);
        var names = builtins.RootElement.GetProperty("data").GetProperty("builtins").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()).ToArray();
        Assert.Equal("console", names[0], "the console shim is the first builtin");
        Assert.Equal("fetch", names[^1], "the fetch trap is the last builtin");
        var (toolValue, toolResult) = await RunAsync(tools, "cordis_inspect_query", JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"platform\":\"host\",\"provider\":\"Tool\",\"method\":\"listTools\"}")!), session);
        using var toolsJson = JsonDocument.Parse(((TextBlock)toolResult.Content[0]).Text);
        var toolNames = toolsJson.RootElement.GetProperty("data").GetProperty("tools").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()).ToArray();
        Assert.Equal("cordis_inspect_list", toolNames[0], "the tool provider lists in registration order");
        Assert.True(toolNames.Contains("cordis_define"), "the cordis tools are visible through the Tool provider");
        Assert.True(toolNames.Contains("cordis_inspect_query"), "cordis_inspect_query is visible through the Tool provider");
    }

    private static async Task<string> RunFailureAsync(ToolRuntime tools, string name, JsonElement args, Dsh.Session.Session session)
    {
        var tool = tools.Get(name) ?? throw new InvalidOperationException($"tool {name} is not registered");
        var execution = await tools.ExecuteAsync(
            new ToolExecutionInput(new ToolCallId("call"), name, args, CancellationToken.None) { Session = session },
            CancellationToken.None);
        Assert.True(execution.IsError, $"tool {name} should have failed");
        return ((ToolExecutionFailure)execution).Error.Message;
    }

    /// <summary>The recorded <c>cordis_inspect_query</c> tool/result text (the corpus pin), read from the scenario fixture.</summary>
    private static string RecordedInspectQueryText()
    {
        var fixture = Path.Combine(RepoRoot(), "snapshots", "session", "cordis-inspect-jsdoc", "session.jsonl");
        foreach (var line in File.ReadLines(fixture))
        {
            if (!line.Contains("\"tool/result\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            var text = document.RootElement.GetProperty("data").GetProperty("message")
                .GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString();
            if (text is not null && text.Contains("\"listService\"", StringComparison.Ordinal)) return text;
        }
        throw new InvalidOperationException("the cordis-inspect-jsdoc fixture has no cordis_inspect_query tool/result");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "snapshots"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static (ToolRuntime Tools, DynamicCordisRunner Runner, Dsh.Session.Session Session) CreateHarness()
    {
        var ctx = new Context();
        var runner = new DynamicCordisRunner();
        var tools = new ToolRuntime(ctx);
        foreach (var tool in CordisTools.Definitions(runner, tools)) tools.Register(tool);
        var store = new SessionStore(ctx);
        var session = store.Create();
        return (tools, runner, session);
    }

    private static async Task<(JsonElement Value, ToolExecutionSuccess Result)> RunAsync(
        ToolRuntime tools, string name, JsonElement args, Dsh.Session.Session session)
    {
        var tool = tools.Get(name) ?? throw new InvalidOperationException($"tool {name} is not registered");
        var execution = await tools.ExecuteAsync(
            new ToolExecutionInput(new ToolCallId("call"), name, args, CancellationToken.None) { Session = session },
            CancellationToken.None);
        if (execution.IsError) throw new Exception($"tool {name} failed: {((ToolExecutionFailure)execution).Error.Message}");
        return (((ToolExecutionSuccess)execution).Value, (ToolExecutionSuccess)execution);
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected} got {actual}");
    }

    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}