using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Lsp;

/// <summary>
/// LSP base-protocol framing (port of <c>framing.ts</c>): the encoder produces one Content-Length-delimited
/// buffer; the decoder buffers incoming bytes and yields complete message envelopes, bounding the header
/// and total message size so a hostile or broken server cannot exhaust memory.
/// </summary>
public static class Framing
{
    /// <summary>Wire JSON: camelCase property names, nulls omitted, non-ASCII kept verbatim (Node JSON semantics).</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Encode one JSON-RPC message as a framed LSP buffer (<c>Content-Length: N\r\n\r\n&lt;utf-8 json&gt;</c>).</summary>
    /// <param name="message">the JSON-RPC message envelope to serialize.</param>
    /// <returns>the framed bytes ready to write to the server's stdin.</returns>
    public static byte[] EncodeMessage(JsonRpcMessage message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, WireJson);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var framed = new byte[header.Length + body.Length];
        header.CopyTo(framed, 0);
        body.CopyTo(framed, header.Length);
        return framed;
    }
}

/// <summary>
/// A streaming decoder for Content-Length-framed JSON-RPC (port of <c>MessageDecoder</c>). Feed it stdout
/// chunks; it returns every whole message envelope that completed, in arrival order. Only the
/// <c>Content-Length</c> header is parsed; other headers (for example <c>Content-Type</c>) are ignored.
/// </summary>
public sealed class MessageDecoder
{
    /// <summary>Cap on the header section so a server that never sends the separator cannot grow the buffer forever.</summary>
    private const int MaxHeaderBytes = 1 << 16;

    private readonly int _maxMessageBytes;
    private byte[] _buffer = new byte[1024];
    private int _count;

    /// <summary>Create the decoder.</summary>
    /// <param name="maxMessageBytes">reject any single framed body larger than this (guards memory).</param>
    public MessageDecoder(int maxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>Append a chunk and return every message envelope that is now complete.</summary>
    /// <param name="chunk">raw bytes from the server's stdout.</param>
    /// <returns>the parsed envelopes, in arrival order (possibly empty).</returns>
    public JsonRpcMessage[] Push(ReadOnlyMemory<byte> chunk)
    {
        Append(chunk.Span);
        var messages = new List<JsonRpcMessage>();
        while (TryReadNext(out var message)) messages.Add(message);
        return messages.ToArray();
    }

    /// <summary>Parse and consume the next complete message, or report that more bytes are needed.</summary>
    private bool TryReadNext(out JsonRpcMessage message)
    {
        message = default!;
        var span = _buffer.AsSpan(0, _count);
        var separator = span.IndexOf("\r\n\r\n"u8);
        if (separator < 0)
        {
            if (_count > MaxHeaderBytes)
            {
                throw new InvalidOperationException($"LSP header exceeded {MaxHeaderBytes} bytes without a terminator");
            }
            return false;
        }
        if (separator > MaxHeaderBytes)
        {
            throw new InvalidOperationException($"LSP header exceeded {MaxHeaderBytes} bytes");
        }
        var headerText = Encoding.ASCII.GetString(_buffer, 0, separator);
        var contentLength = ParseContentLength(headerText);
        if (contentLength > _maxMessageBytes)
        {
            throw new InvalidOperationException($"LSP message length {contentLength} exceeds the {_maxMessageBytes}-byte limit");
        }
        var bodyStart = separator + 4;
        var bodyEnd = bodyStart + contentLength;
        if (_count < bodyEnd) return false;
        var body = Encoding.UTF8.GetString(_buffer, bodyStart, contentLength);
        Consume(bodyEnd);
        try
        {
            using var document = JsonDocument.Parse(body);
            message = LspTransport.Deserialize(document.RootElement);
            return true;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"LSP message body was not valid JSON: {error.Message}", error);
        }
    }

    /// <summary>Read the Content-Length header value (case-insensitive), rejecting a missing or non-numeric one.</summary>
    private static int ParseContentLength(string headerText)
    {
        var found = false;
        var length = 0;
        foreach (var line in headerText.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (!string.Equals(line[..colon].Trim(), "content-length", StringComparison.OrdinalIgnoreCase)) continue;
            var text = line[(colon + 1)..].Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out length) || length < 0)
            {
                throw new InvalidOperationException($"invalid Content-Length header: \"{line}\"");
            }
            found = true;
        }
        if (!found)
        {
            throw new InvalidOperationException($"LSP header block missing Content-Length: \"{headerText}\"");
        }
        return length;
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_count + chunk.Length > _buffer.Length)
        {
            var grown = new byte[Math.Max(_buffer.Length * 2, _count + chunk.Length)];
            _buffer.AsSpan(0, _count).CopyTo(grown);
            _buffer = grown;
        }
        chunk.CopyTo(_buffer.AsSpan(_count));
        _count += chunk.Length;
    }

    private void Consume(int bytes)
    {
        var remaining = _count - bytes;
        if (remaining > 0) Buffer.BlockCopy(_buffer, bytes, _buffer, 0, remaining);
        _count = remaining;
    }
}
