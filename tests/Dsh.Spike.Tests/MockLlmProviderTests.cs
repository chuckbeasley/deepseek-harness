using Harness.Llm;
using Harness.Spike;

namespace Harness.Spike.Tests;

public static class MockLlmProviderTests
{
    private static GenerateOptions Request()
        => new(MockLlmProvider.Provider, MockLlmProvider.Model, Array.Empty<Message>());

    /// <summary>Blocking drain of one adapter stream (the runner is synchronous).</summary>
    private static IReadOnlyList<StreamChunk> Drain(MockLlmProvider provider, GenerateOptions request)
    {
        var chunks = new List<StreamChunk>();
        var enumerator = provider.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        while (true)
        {
            if (!enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) break;
            chunks.Add(enumerator.Current);
        }
        return chunks;
    }

    public static void FirstCall_StreamsOneTodoWriteToolCall()
    {
        var provider = new MockLlmProvider();
        var assembler = new BlockAssembler();
        foreach (var chunk in Drain(provider, Request())) assembler.Push(chunk);

        var block = Assert.IsType<ToolCallBlock>(Assert.Single(assembler.Blocks()));
        Assert.Equal("call-1", block.Id.Value);
        Assert.Equal("todo_write", block.Name);
        Assert.Equal(MockLlmProvider.ToolCallArguments, block.Arguments);
        Assert.True(assembler.Finish is ToolCalls, "finish should be tool-calls");
        Assert.Equal(1, provider.CallCount);
    }

    public static void SecondCall_StreamsPlainTextAndStops()
    {
        var provider = new MockLlmProvider();
        var first = new BlockAssembler();
        foreach (var chunk in Drain(provider, Request())) first.Push(chunk);

        var assembler = new BlockAssembler();
        foreach (var chunk in Drain(provider, Request())) assembler.Push(chunk);

        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks()));
        Assert.Equal("Todo list recorded.", block.Text);
        Assert.True(assembler.Finish is Stop, "finish should be stop");
        Assert.Equal(2, provider.CallCount);
    }

    public static void CancelledToken_AbortsBeforeAnyChunk()
    {
        var provider = new MockLlmProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            var enumerator = provider.StreamAsync(Request(), cts.Token).GetAsyncEnumerator();
            return enumerator.MoveNextAsync().AsTask();
        });
    }
}
