using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Session;
using Harness.Tools;

namespace Harness.Code.Tests;

/// <summary>The run_code tool over a real ToolRuntime with a fake echo tool.</summary>
public static class RunCodeToolTests
{
    public static async Task RecordedProgram_ExecutesAndRenders()
    {
        var (tool, runtime) = CreateHarness();
        var (value, _) = await tool.RunWith(Args("const r = await tools.echo({ text: 'BOTH_OK' });\nreturn r.text;"), runtime);
        Assert.Equal("BOTH_OK", value, "the recorded program returns the echoed text verbatim");
    }

    public static async Task ConsoleOutput_PrefixesTheReturnValue()
    {
        var (tool, runtime) = CreateHarness();
        var (value, _) = await tool.RunWith(Args("console.log('captured');\nreturn 'CODE_ONE+CODE_TWO';"), runtime);
        Assert.Equal("captured\nCODE_ONE+CODE_TWO", value, "the captured log precedes the return value");
    }

    public static async Task DispatchedCalls_RecordTheDispatchPairs()
    {
        var (tool, runtime) = CreateHarness();
        var (_, session) = await tool.RunWith(Args("await tools.echo({ text: 'x' });\nawait tools.echo({ text: 'y' });\nreturn 'done';"), runtime);
        var starts = session.Events.OfType<ToolCodeDispatchStartEvent>().ToArray();
        var ends = session.Events.OfType<ToolCodeDispatchEvent>().ToArray();
        Assert.Equal(2, starts.Length, "two dispatch-start records");
        Assert.Equal(2, ends.Length, "two dispatch records");
        Assert.Equal("call:code:1", starts[0].SubCallId, "the sub-call id is root:code:n");
        Assert.Equal("call:code:2", starts[1].SubCallId, "the second sub-call id increments");
        Assert.Equal("echo", starts[0].Name, "the dispatched tool name is recorded");
        Assert.Equal("x", starts[0].Arguments.GetProperty("text").GetString(), "the dispatched arguments are recorded");
        Assert.False(ends[0].IsError, "the dispatched call succeeded");
    }

    public static async Task ObjectReturn_RendersAsTwoSpaceJson()
    {
        var (tool, runtime) = CreateHarness();
        var (value, _) = await tool.RunWith(Args("return { reply: 'WF_CHILD_OK' };"), runtime);
        Assert.Equal("{\n  \"reply\": \"WF_CHILD_OK\"\n}", value, "an object return renders as two-space JSON");
    }

    private static (ToolDefinition Tool, ToolRuntime Runtime) CreateHarness()
    {
        var ctx = new Context();
        var runtime = new ToolRuntime(ctx);
        runtime.Register(new ToolDefinition(
            Name: "echo",
            Description: "Echo text.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"text\":{\"type\":\"string\",\"required\":true}}")!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}")!),
            Execute: (args, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { text = args.GetProperty("text").GetString() ?? "" })),
            Render: (_, value) => new ContentBlock[] { new TextBlock((value.GetProperty("text").GetString() ?? "") + "\n") }));
        return (RunCodeTool.Definition(runtime), runtime);
    }

    private static JsonElement Args(string code)
        => JsonSerializer.SerializeToElement(new { code, description = "test program" });

    private static async Task<(string Text, global::Harness.Session.Session Session)> RunWith(this ToolDefinition tool, JsonElement args, ToolRuntime runtime)
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);
        var session = store.Create();
        var value = await tool.Execute(args, new ToolRunContext(new ToolCallId("call"), "run_code", args, CancellationToken.None) { Session = session });
        return (value.GetString() ?? string.Empty, session);
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

    public static void False(bool condition, string message) => True(!condition, message);
}
