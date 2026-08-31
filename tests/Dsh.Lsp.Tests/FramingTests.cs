using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Dsh.Lsp.Tests;

/// <summary>The Content-Length encoder and the stateful streaming decoder (mirrors framing.spec.ts).</summary>
public static class FramingTests
{
    /// <summary>Non-ASCII stays verbatim, matching the JSON.stringify semantics the encoder mirrors.</summary>
    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Frame a raw JSON body the way a server would, for decoder round-trips.</summary>
    private static byte[] Frame(string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        var result = new byte[header.Length + bodyBytes.Length];
        header.CopyTo(result, 0);
        bodyBytes.CopyTo(result, header.Length);
        return result;
    }

    public static Task Encode_PrefixesContentLengthHeaderWithUtf8ByteLength()
    {
        var message = new JsonRpcMessage(Method: "x", Params: JsonSerializer.SerializeToElement(new { s = "é" }, Relaxed));
        var bytes = Framing.EncodeMessage(message);
        var body = "{\"jsonrpc\":\"2.0\",\"method\":\"x\",\"params\":{\"s\":\"é\"}}";
        var expected = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        Assert.True(bytes.AsSpan().SequenceEqual(expected), "the frame is the Content-Length header (UTF-8 byte length) plus the body");
        return Task.CompletedTask;
    }

    public static Task Decode_SingleFramedMessage()
    {
        var decoder = new MessageDecoder(1_000);
        var messages = decoder.Push(Frame("{\"id\":1,\"result\":42}"));
        Assert.Equal(1, messages.Length, "one message is decoded");
        Assert.Equal(1L, messages[0].Id, "the id round-trips");
        Assert.True(messages[0].Result.HasValue && messages[0].Result.Value.GetInt64() == 42, "the result round-trips");
        return Task.CompletedTask;
    }

    public static Task Decode_MultipleMessagesInOneChunk()
    {
        var decoder = new MessageDecoder(1_000);
        var chunk = Frame("{\"id\":1,\"result\":{\"a\":1}}").Concat(Frame("{\"id\":2,\"result\":{\"b\":2}}")).ToArray();
        var messages = decoder.Push(chunk);
        Assert.Equal(2, messages.Length, "both messages are decoded from one chunk");
        Assert.Equal(1L, messages[0].Id, "the first message is first");
        Assert.Equal(2L, messages[1].Id, "the second message is second");
        return Task.CompletedTask;
    }

    public static Task Decode_ReassemblesMessageSplitAcrossChunks()
    {
        var decoder = new MessageDecoder(1_000);
        var full = Frame("{\"method\":\"hello\",\"params\":{\"world\":\"there\"}}");
        var first = decoder.Push(full.AsMemory(0, 10));
        Assert.Equal(0, first.Length, "a partial chunk yields nothing");
        var messages = decoder.Push(full.AsMemory(10));
        Assert.Equal(1, messages.Length, "the remainder completes the message");
        Assert.Equal("hello", messages[0].Method, "the method round-trips");
        Assert.Equal("there", messages[0].Params!.Value.GetProperty("world").GetString(), "the payload round-trips");
        return Task.CompletedTask;
    }

    public static Task Decode_HandlesHeaderSplitFromBody()
    {
        var decoder = new MessageDecoder(1_000);
        var body = "{\"method\":\"x\",\"params\":{\"x\":1}}";
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        Assert.Equal(0, decoder.Push(header).Length, "the header alone yields nothing");
        var messages = decoder.Push(Encoding.UTF8.GetBytes(body));
        Assert.Equal(1, messages.Length, "the body chunk completes the message");
        Assert.Equal("x", messages[0].Method, "the method round-trips");
        Assert.True(messages[0].Params!.Value.GetProperty("x").GetInt64() == 1, "the payload round-trips");
        return Task.CompletedTask;
    }

    public static Task Decode_ReadsCaseInsensitiveHeaderAndIgnoresOtherHeaders()
    {
        var decoder = new MessageDecoder(1_000);
        var body = "{\"method\":\"ok\",\"params\":true}";
        var chunk = Encoding.UTF8.GetBytes($"content-length: {body.Length}\r\nContent-Type: x\r\n\r\n{body}");
        var messages = decoder.Push(chunk);
        Assert.Equal(1, messages.Length, "the frame decodes despite other headers and casing");
        Assert.Equal("ok", messages[0].Method, "the method round-trips");
        Assert.True(messages[0].Params!.Value.GetBoolean(), "the payload round-trips");
        return Task.CompletedTask;
    }

    public static Task Decode_RejectsBodyOverSizeLimit()
    {
        var decoder = new MessageDecoder(4);
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Push(Frame("{\"big\":true}")));
        Assert.Contains("exceeds the 4-byte limit", error.Message, "the body-size error is exact");
        return Task.CompletedTask;
    }

    public static Task Decode_RejectsMissingContentLength()
    {
        var decoder = new MessageDecoder(1_000);
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Push(Encoding.UTF8.GetBytes("X: 1\r\n\r\n{}")));
        Assert.Contains("missing Content-Length", error.Message, "the missing-header error is exact");
        return Task.CompletedTask;
    }

    public static Task Decode_RejectsNonNumericContentLength()
    {
        var decoder = new MessageDecoder(1_000);
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Push(Encoding.UTF8.GetBytes("Content-Length: abc\r\n\r\n{}")));
        Assert.Contains("invalid Content-Length", error.Message, "the non-numeric error is exact");
        return Task.CompletedTask;
    }

    public static Task Decode_RejectsUnterminatedHeaderBlock()
    {
        var decoder = new MessageDecoder(1_000);
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Push(new byte[65537]));
        Assert.Contains("without a terminator", error.Message, "the unterminated-header error is exact");
        return Task.CompletedTask;
    }

    public static Task Decode_RejectsOversizedHeaderWithTerminator()
    {
        var decoder = new MessageDecoder(1_000);
        var chunk = Encoding.ASCII.GetBytes($"Content-Length: 2\r\nX-Fill: {new string('a', 70_000)}\r\n\r\n{{}}");
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Push(chunk));
        Assert.Contains("header exceeded", error.Message, "the oversized-header error is exact");
        return Task.CompletedTask;
    }

    public static Task Decode_RejectsNonJsonBody()
    {
        var decoder = new MessageDecoder(1_000);
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Push(Frame("not json")));
        Assert.Contains("not valid JSON", error.Message, "the non-JSON error is exact");
        return Task.CompletedTask;
    }
}
