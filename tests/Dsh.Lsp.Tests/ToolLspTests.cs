using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Tools;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Lsp.Tests;

/// <summary>The mounted lsp tool path: routing service, fixture provider, and the recorded render.</summary>
public static class ToolLspTests
{
    public static async Task ToolLsp_QueriesThroughTheService_AndRendersTheRecording()
    {
        using var ctx = new Context();
        var service = new LspService(ctx);
        var provider = new FixtureLspProvider(new LspProviderId("fixture"), new Dictionary<string, string> { [".ts"] = "typescript" });
        var registration = service.RegisterProvider(provider);
        var tool = ToolLsp.Definition(service, maxLocations: 1);
        try
        {
            var args = JsonSerializer.SerializeToElement(JsonNode.Parse("{\"operation\":\"goToDefinition\",\"file_path\":\"subject.ts\",\"line\":1,\"character\":7}")!);
            var value = await tool.Execute(args, new ToolRunContext(new ToolCallId("c"), "lsp", args, CancellationToken.None));
            var blocks = tool.Render!(args, value);
            Assert.Equal("subject.ts:1:7\n\u2026 1 more location omitted (limit 1).", ((TextBlock)blocks[0]).Text, "the tool renders the recorded line");
        }
        finally
        {
            registration();
        }
    }

    public static Task LspService_RoutesByFinalExtension()
    {
        using var ctx = new Context();
        var service = new LspService(ctx);
        var provider = new FixtureLspProvider(new LspProviderId("fixture"), new Dictionary<string, string> { [".ts"] = "typescript" });
        var registration = service.RegisterProvider(provider);
        try
        {
            var error = Assert.Throws<LspError>(() => service.QueryAsync(new LspQueryRequest(
                LspOperation.GoToDefinition, "notes.md", new LspPosition(0, 0), WorkspaceRoot: ".")).GetAwaiter().GetResult());
            Assert.True(error.Code == "LSP_NO_PROVIDER", "an unowned extension routes to no provider");
        }
        finally
        {
            registration();
        }
        return Task.CompletedTask;
    }
}