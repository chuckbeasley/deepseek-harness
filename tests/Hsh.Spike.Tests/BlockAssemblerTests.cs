using Harness.Llm;

namespace Harness.Spike.Tests;

public static class BlockAssemblerTests
{
    public static void ToolCallStream_AssemblesOneToolCallBlock()
    {
        var assembler = new BlockAssembler();
        var id = new ToolCallId("call-1");
        const string args = "{\"todos\":[]}";
        assembler.Push(new BlockStart(0, "tool-call"));
        assembler.Push(new ToolCallDelta(0, id, "todo_write", args));
        assembler.Push(new BlockEnd(0, new ToolCallBlock(id, "todo_write", args)));
        assembler.Push(new Finish(new ToolCalls()));

        var block = Assert.IsType<ToolCallBlock>(Assert.Single(assembler.Blocks()));
        Assert.Equal("todo_write", block.Name);
        Assert.Equal(args, block.Arguments);
        Assert.Equal(id, block.Id);
        Assert.True(assembler.Finish is ToolCalls, "finish should be tool-calls");
    }

    public static void TextStream_AssemblesFromDeltas_WithoutBlockEnd()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new BlockStart(0, "text"));
        assembler.Push(new TextDelta(0, "Todo list "));
        assembler.Push(new TextDelta(0, "recorded."));

        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks()));
        Assert.Equal("Todo list recorded.", block.Text);
        Assert.True(assembler.Finish is Stop, "finish should default to stop");
        Assert.Null(assembler.Usage);
    }

    public static void InterruptedBlocks_KeepTextPrefix_AndDropToolCalls()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new BlockStart(0, "text"));
        assembler.Push(new TextDelta(0, "partial"));
        assembler.Push(new BlockStart(1, "tool-call"));
        assembler.Push(new ToolCallDelta(1, new ToolCallId("call-1"), "todo_write", "{}"));

        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.InterruptedBlocks()));
        Assert.Equal("partial", block.Text);
    }

    public static void MaxTokensFinish_DropsToolCalls()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new BlockStart(0, "text"));
        assembler.Push(new TextDelta(0, "done"));
        assembler.Push(new BlockStart(1, "tool-call"));
        assembler.Push(new ToolCallDelta(1, new ToolCallId("call-1"), "todo_write", "{}"));
        assembler.Push(new BlockEnd(1, new ToolCallBlock(new ToolCallId("call-1"), "todo_write", "{}")));
        assembler.Push(new Finish(new MaxTokens()));

        var blocks = assembler.Blocks();
        Assert.Equal(1, blocks.Count);
        Assert.True(blocks[0] is TextBlock, "the kept block should be the text block");
    }

    public static void UsageChunk_IsRetained()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new UsageChunk(new TokenUsage(10, 5)));
        Assert.Equal(new TokenUsage(10, 5), assembler.Usage);
    }

    public static void StragglerDelta_AfterBlockEnd_IsIgnored()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new BlockStart(0, "text"));
        assembler.Push(new BlockEnd(0, new TextBlock("final")));
        assembler.Push(new TextDelta(0, "straggler"));

        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks()));
        Assert.Equal("final", block.Text);
    }
}

