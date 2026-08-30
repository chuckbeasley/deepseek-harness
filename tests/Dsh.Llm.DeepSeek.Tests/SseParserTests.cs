using System.Text;

namespace Dsh.Llm.DeepSeek.Tests;

/// <summary>SSE frame parsing tests: framing, terminators, comments, BOM, and the [DONE] contract.</summary>
public static class SseParserTests
{
    private static async Task<List<string>> Parse(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var result = new List<string>();
        try
        {
            await foreach (var payload in SseParser.ParseAsync(stream, CancellationToken.None))
            {
                result.Add(payload);
            }
        }
        catch (LlmError error) when (error.Code == "STREAM_CLOSED")
        {
            // EOF without [DONE] is the parser's normal terminal state for plain fixtures.
        }
        return result;
    }

    private static LlmError CaptureParseError(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var enumerator = SseParser.ParseAsync(stream, CancellationToken.None).GetAsyncEnumerator();
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

    public static void MultiLineData_JoinsWithNewline()
    {
        var payloads = Parse("data: line1\ndata: line2\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "line1\nline2" }, payloads);
    }

    public static void CommentsAndNonDataFields_AreSkipped()
    {
        var payloads = Parse(": ping\nevent: message\nid: 7\ndata: x\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "x" }, payloads);
    }

    public static void CrlfTerminators_AreHandled()
    {
        var payloads = Parse("data: x\r\n\r\ndata: y\r\n\r\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "x", "y" }, payloads);
    }

    public static void BOM_IsStripped()
    {
        var payloads = Parse("\uFEFFdata: x\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "x" }, payloads);
    }

    public static void MultipleEvents_YieldInOrder()
    {
        var payloads = Parse("data: a\n\ndata: b\n\ndata: c\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "a", "b", "c" }, payloads);
    }

    public static void Done_StopsParsing()
    {
        var payloads = Parse("data: a\n\ndata: [DONE]\n\ndata: b\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "a", "[DONE]" }, payloads);
    }

    public static void UnterminatedTail_AtEof_IsTruncation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: a\n\ndata: b"));
        var enumerator = SseParser.ParseAsync(stream, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Assert.True(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "first event must dispatch");
            Assert.Equal("a", enumerator.Current);
            var error = Assert.Throws<LlmError>(() => enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult());
            Assert.Equal("STREAM_CLOSED", error.Code);
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void MissingDone_ThrowsStreamClosed()
    {
        var error = CaptureParseError("data: a\n\n");
        Assert.Equal("STREAM_CLOSED", error.Code);
    }

    public static void EmptyDataField_YieldsEmptyPayload()
    {
        var payloads = Parse("data:\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { string.Empty }, payloads);
    }

    public static void LineWithoutColon_IsIgnored()
    {
        var payloads = Parse("not a field\ndata: x\n\n").GetAwaiter().GetResult();
        Assert.Equal(new[] { "x" }, payloads);
    }
}
