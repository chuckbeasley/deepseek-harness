using Dsh.Llm;

namespace Dsh.Llm.DeepSeek.Tests;

/// <summary>Payload-to-StreamChunk translation tests: block opening, finish reasons, usage, and failures.</summary>
public static class TranslateTests
{
    private static List<StreamChunk> Drain(params string[] payloads)
    {
        var chunks = new List<StreamChunk>();
        var enumerator = Translate.Run(Payloads(payloads)).GetAsyncEnumerator();
        while (true)
        {
            if (!enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) break;
            chunks.Add(enumerator.Current);
        }
        return chunks;
    }

    private static async IAsyncEnumerable<string> Payloads(params string[] payloads)
    {
        foreach (var payload in payloads) yield return payload;
        await Task.CompletedTask;
    }

    private static LlmError CaptureError(params string[] payloads)
    {
        var enumerator = Translate.Run(Payloads(payloads)).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                var moved = enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
                if (!moved) break;
            }
        }
        catch (LlmError error)
        {
            return error;
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        throw new AssertionException("expected LlmError but the stream completed");
    }

    public static void TextStream_YieldsBlocksAndStop()
    {
        var chunks = Drain("""{"choices":[{"delta":{"content":"Hi"}}]}""", "[DONE]");
        Assert.Equal(4, chunks.Count);
        var start = Assert.IsType<BlockStart>(chunks[0]);
        Assert.Equal(0, start.Index);
        Assert.Equal("text", start.BlockType);
        Assert.Equal("Hi", Assert.IsType<TextDelta>(chunks[1]).Text);
        var blockEnd = Assert.IsType<BlockEnd>(chunks[2]);
        Assert.Equal("Hi", Assert.IsType<TextBlock>(blockEnd.Block).Text);
        Assert.True(Assert.IsType<Finish>(chunks[3]).Reason is Stop, "finish must be stop");
    }

    public static void ReasoningThenText_OpenSeparateBlocks()
    {
        var chunks = Drain(
            """{"choices":[{"delta":{"reasoning_content":"think"}}]}""",
            """{"choices":[{"delta":{"content":"answer"}}]}""",
            "[DONE]");
        Assert.Equal(7, chunks.Count);
        var reasoningStart = Assert.IsType<BlockStart>(chunks[0]);
        Assert.Equal(0, reasoningStart.Index);
        Assert.Equal("reasoning", reasoningStart.BlockType);
        Assert.Equal("think", Assert.IsType<ReasoningDelta>(chunks[1]).Text);
        var textStart = Assert.IsType<BlockStart>(chunks[2]);
        Assert.Equal(1, textStart.Index);
        Assert.Equal("text", textStart.BlockType);
        Assert.Equal("answer", Assert.IsType<TextDelta>(chunks[3]).Text);
        Assert.Equal("think", Assert.IsType<ReasoningBlock>(Assert.IsType<BlockEnd>(chunks[4]).Block).Text);
        Assert.Equal("answer", Assert.IsType<TextBlock>(Assert.IsType<BlockEnd>(chunks[5]).Block).Text);
        Assert.True(Assert.IsType<Finish>(chunks[6]).Reason is Stop, "finish must be stop");
    }

    public static void EmptyInitialReasoningDelta_DoesNotOpenBlock()
    {
        var chunks = Drain(
            """{"choices":[{"delta":{"reasoning_content":""}}]}""",
            """{"choices":[{"delta":{"reasoning_content":"think"}}]}""",
            "[DONE]");
        Assert.Equal(4, chunks.Count);
        var start = Assert.IsType<BlockStart>(chunks[0]);
        Assert.Equal(0, start.Index);
        Assert.Equal("reasoning", start.BlockType);
        Assert.Equal("think", Assert.IsType<ReasoningDelta>(chunks[1]).Text);
    }

    public static void ToolCallDeltas_ConcatenateIntoOneBlock()
    {
        var chunks = Drain(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"todo_write","arguments":"{\"todos\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"[]}"}}]}}]}""",
            "[DONE]");
        Assert.Equal(5, chunks.Count);
        Assert.Equal("tool-call", Assert.IsType<BlockStart>(chunks[0]).BlockType);
        var first = Assert.IsType<ToolCallDelta>(chunks[1]);
        Assert.Equal("call_a", first.Id.Value);
        Assert.Equal("todo_write", first.Name);
        Assert.Equal("{\"todos\":", first.ArgumentsDelta);
        Assert.Equal("[]}", Assert.IsType<ToolCallDelta>(chunks[2]).ArgumentsDelta);
        var block = Assert.IsType<ToolCallBlock>(Assert.IsType<BlockEnd>(chunks[3]).Block);
        Assert.Equal("call_a", block.Id.Value);
        Assert.Equal("todo_write", block.Name);
        Assert.Equal("{\"todos\":[]}", block.Arguments);
        Assert.True(Assert.IsType<Finish>(chunks[4]).Reason is Stop, "finish must be stop");
    }

    public static void ParallelToolCalls_OpenSeparateBlocks()
    {
        var chunks = Drain(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"f1","arguments":"{"}},{"index":1,"id":"b","function":{"name":"f2","arguments":"{"}}]}}]}""",
            "[DONE]");
        Assert.Equal(7, chunks.Count);
        Assert.Equal(0, Assert.IsType<BlockStart>(chunks[0]).Index);
        Assert.Equal("a", Assert.IsType<ToolCallDelta>(chunks[1]).Id.Value);
        Assert.Equal(1, Assert.IsType<BlockStart>(chunks[2]).Index);
        Assert.Equal("b", Assert.IsType<ToolCallDelta>(chunks[3]).Id.Value);
        Assert.Equal("a", Assert.IsType<ToolCallBlock>(Assert.IsType<BlockEnd>(chunks[4]).Block).Id.Value);
        Assert.Equal("b", Assert.IsType<ToolCallBlock>(Assert.IsType<BlockEnd>(chunks[5]).Block).Id.Value);
    }

    public static void FinishReason_Stop()
    {
        var chunks = Drain("""{"choices":[{"delta":{"content":"x"},"finish_reason":"stop"}]}""", "[DONE]");
        Assert.True(Assert.IsType<Finish>(chunks[^1]).Reason is Stop, "stop must map to Stop");
    }

    public static void FinishReason_ToolCalls()
    {
        var chunks = Drain("""{"choices":[{"delta":{"content":"x"},"finish_reason":"tool_calls"}]}""", "[DONE]");
        Assert.True(Assert.IsType<Finish>(chunks[^1]).Reason is ToolCalls, "tool_calls must map to ToolCalls");
    }

    public static void FinishReason_Length()
    {
        var chunks = Drain("""{"choices":[{"delta":{"content":"x"},"finish_reason":"length"}]}""", "[DONE]");
        Assert.True(Assert.IsType<Finish>(chunks[^1]).Reason is MaxTokens, "length must map to MaxTokens");
    }

    public static void UnknownFinishReason_MapsToError()
    {
        var chunks = Drain("""{"choices":[{"delta":{"content":"x"},"finish_reason":"content_filter"}]}""", "[DONE]");
        var failure = Assert.IsType<Error>(Assert.IsType<Finish>(chunks[^1]).Reason).Failure;
        Assert.Equal("model stopped: content_filter", failure.Message);
        Assert.Equal("CONTENT_FILTER", failure.Code);
    }

    public static void Usage_MapsToDisjointCounts()
    {
        var chunks = Drain(
            """{"choices":[{"delta":{"content":"hi"}}]}""",
            """{"usage":{"prompt_tokens":100,"completion_tokens":25,"total_tokens":125,"prompt_tokens_details":{"cached_tokens":40},"completion_tokens_details":{"reasoning_tokens":5}}}""",
            "[DONE]");
        var usage = Assert.IsType<UsageChunk>(chunks[^2]).Usage;
        Assert.Equal(60, usage.InputTokens); // prompt_tokens minus cache hits
        Assert.Equal(25, usage.OutputTokens);
        Assert.Equal(125, usage.TotalTokens);
        Assert.Equal(40, usage.CacheReadTokens);
        Assert.Equal(5, usage.ReasoningTokens);
        Assert.Null(usage.CacheWriteTokens);
    }

    public static void TrailingUsageOnlyChunk_IsEmitted()
    {
        var chunks = Drain(
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""",
            """{"usage":{"prompt_tokens":20,"completion_tokens":7}}""",
            "[DONE]");
        var usage = Assert.IsType<UsageChunk>(chunks[^2]).Usage;
        Assert.Equal(20, usage.InputTokens);
        Assert.Equal(7, usage.OutputTokens);
        Assert.True(Assert.IsType<Finish>(chunks[^1]).Reason is Stop, "finish must still be stop");
    }

    public static void EmptyResponse_FinishIsError()
    {
        var chunks = Drain("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""", "[DONE]");
        var failure = Assert.IsType<Error>(Assert.IsType<Finish>(Assert.Single(chunks)).Reason).Failure;
        Assert.Equal("EMPTY_RESPONSE", failure.Code);
    }

    public static void MalformedPayload_ThrowsMalformedResponse()
    {
        var error = CaptureError("not json");
        Assert.Equal("MALFORMED_RESPONSE", error.Code);
    }

    public static void MissingDone_ThrowsStreamClosed()
    {
        var error = CaptureError("""{"choices":[{"delta":{"content":"x"}}]}""");
        Assert.Equal("STREAM_CLOSED", error.Code);
    }
}
