namespace Harness.Workflow.Tests;

/// <summary>The workflow tool's label rule, interpreter object parsing, and pretty rendering.</summary>
public static class WorkflowToolTests
{
    public static void DefaultLabel_TruncatesThePromptFirstLineAt47()
    {
        Assert.Equal("short prompt", WorkflowTool.DefaultLabel("short prompt"), "a short prompt keeps its full first line");
        var longLine = new string('x', 100);
        Assert.Equal(new string('x', 47) + "…", WorkflowTool.DefaultLabel(longLine), "a long first line truncates at 47 with the ellipsis");
        var multiline = "first line\nsecond";
        Assert.Equal("first line", WorkflowTool.DefaultLabel(multiline), "only the first line is used");
    }

    public static void PrettyJson_MatchesTheRecordedSpelling()
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse("{\"reply\":\"WF_CHILD_OK\"}")!.AsObject();
        Assert.Equal("{\n  \"reply\": \"WF_CHILD_OK\"\n}", WorkflowTool.PrettyJson(value), "the two-space LF JSON matches the recording");
    }
}