using System.Text.Json.Nodes;
using Harness.Workflow;

namespace Harness.Workflow.Tests;

/// <summary>The Ralph tool's fixed prompt, report validation, and result rendering.</summary>
public static class RalphToolTests
{
    public static void BuildPrompt_MatchesTheRecordedWorkerWording()
    {
        var prompt = RalphTool.BuildPrompt("Prove two fresh Ralph rounds through the shipped headless app.", 2, 2,
            "{\"status\":\"continue\",\"summary\":\"ROUND_ONE_HANDOFF\"}");
        Assert.True(prompt.StartsWith("You are one fresh worker in a foreground Ralph loop.", StringComparison.Ordinal),
            "the prompt opens with the fresh-worker framing");
        Assert.True(prompt.Contains("Ralph round: 2 of 2.", StringComparison.Ordinal), "the prompt names the round");
        Assert.True(prompt.Contains("Previous structured handoff:\n{\"status\":\"continue\"", StringComparison.Ordinal),
            "the previous handoff embeds the compact JSON report");
    }

    public static void ValidateReport_AcceptsTheRecordedReports()
    {
        var roundOne = JsonNode.Parse("{\"status\":\"continue\",\"summary\":\"ROUND_ONE_HANDOFF\",\"evidence\":[\"Round one inspected the workspace.\"],\"nextSteps\":[\"Finish the snapshot objective.\"],\"blocker\":\"\"}")!.AsObject();
        Assert.True(RalphTool.ValidateReportForTest(roundOne), "a continuing report with nextSteps and an empty blocker validates");
        var roundTwo = JsonNode.Parse("{\"status\":\"complete\",\"summary\":\"The Ralph snapshot objective is complete.\",\"evidence\":[\"Two fresh rounds completed through the shipped app.\"],\"nextSteps\":[],\"blocker\":\"\"}")!.AsObject();
        Assert.True(RalphTool.ValidateReportForTest(roundTwo), "a complete report with evidence and no nextSteps validates");
    }

    public static void ValidateReport_RejectsMalformedReports()
    {
        var invalidStatus = JsonNode.Parse("{\"status\":\"maybe\",\"summary\":\"s\",\"evidence\":[\"e\"],\"nextSteps\":[],\"blocker\":\"\"}")!.AsObject();
        Assert.Throws<InvalidOperationException>(() => RalphTool.ValidateReportForTest(invalidStatus));
        var continuingWithoutNext = JsonNode.Parse("{\"status\":\"continue\",\"summary\":\"s\",\"evidence\":[\"e\"],\"nextSteps\":[],\"blocker\":\"\"}")!.AsObject();
        Assert.Throws<InvalidOperationException>(() => RalphTool.ValidateReportForTest(continuingWithoutNext));
        var unnormalized = JsonNode.Parse("{\"status\":\"complete\",\"summary\":\" s \",\"evidence\":[\"e\"],\"nextSteps\":[],\"blocker\":\"\"}")!.AsObject();
        Assert.Throws<InvalidOperationException>(() => RalphTool.ValidateReportForTest(unnormalized));
        var wrongKeys = JsonNode.Parse("{\"status\":\"complete\",\"summary\":\"s\",\"evidence\":[\"e\"],\"nextSteps\":[],\"blocker\":\"\",\"extra\":1}")!.AsObject();
        Assert.Throws<InvalidOperationException>(() => RalphTool.ValidateReportForTest(wrongKeys));
    }

    public static void StructuredOutputTool_RendersTheRecordedText()
    {
        var tool = RalphTool.StructuredOutputDefinition();
        var args = System.Text.Json.JsonSerializer.SerializeToElement(JsonNode.Parse("{\"status\":\"complete\",\"summary\":\"s\",\"evidence\":[\"e\"],\"nextSteps\":[],\"blocker\":\"\"}")!);
        var value = tool.Execute(args, new global::Harness.Tools.ToolRunContext(new global::Harness.Llm.ToolCallId("c"), "structured_output", args, CancellationToken.None)).GetAwaiter().GetResult();
        var blocks = tool.Render!(args, value);
        Assert.Equal("Structured output recorded.", ((Harness.Llm.TextBlock)blocks[0]).Text, "the render is the recorded line");
    }

    public static void RenderResult_MatchesTheRecordedEnvelope()
    {
        var report = JsonNode.Parse("{\"status\":\"complete\",\"summary\":\"The Ralph snapshot objective is complete.\",\"evidence\":[\"Two fresh rounds completed through the shipped app.\"],\"nextSteps\":[],\"blocker\":\"\"}")!.AsObject();
        var text = RalphTool.RenderResultForTest("complete", 2, report);
        Assert.Equal("Ralph worker reported completion after 2 rounds.\nFinal report:\n{\n  \"status\": \"complete\",\n  \"summary\": \"The Ralph snapshot objective is complete.\",\n  \"evidence\": [\n    \"Two fresh rounds completed through the shipped app.\"\n  ],\n  \"nextSteps\": [],\n  \"blocker\": \"\"\n}", text, "the envelope and the LF two-space pretty JSON match the recording");
    }
}
