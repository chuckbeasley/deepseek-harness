using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Lsp.Tests;

/// <summary>The Content-Length framing and the JSON-RPC client dispatch over in-memory duplexes.</summary>
public static class LspTests
{

    public static async Task Transport_RoundTripsAFramedMessage()
    {
        var inbound = new QueueStream();
        var transport = new LspTransport(inbound, inbound);
        var payload = new JsonObject { ["key"] = "value" };
        var message = new JsonRpcMessage(Method: "test/notify", Params: JsonSerializer.SerializeToElement(payload));
        await transport.WriteAsync(message);
        var read = await transport.ReadAsync();
        Assert.Equal("test/notify", read!.Method, "the method round-trips");
        Assert.Equal("value", read.Params!.Value.GetProperty("key").GetString(), "the params round-trip");
    }

    public static async Task Client_RequestGetsItsResponse()
    {
        var clientToServer = new QueueStream();
        var serverToClient = new QueueStream();
        var client = new LspClient(serverToClient, clientToServer);
        // Mock server: answer the first request with a result.
        var server = Task.Run(async () =>
        {
            var transport = new LspTransport(clientToServer, serverToClient);
            var request = await transport.ReadAsync();
            await transport.WriteAsync(new JsonRpcMessage(Id: request!.Id, Result: JsonSerializer.SerializeToElement(new JsonObject { ["capabilities"] = new JsonObject() })));
        });
        var request = client.RequestAsync("initialize", null);
        var response = await client.ReceiveOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1L, response!.Id, "the response carries the request id");
        Assert.True(response.Result!.Value.TryGetProperty("capabilities", out _), "the result payload round-trips");
        var resolved = await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1L, resolved.Id, "the pending request resolves with the same message");
    }

    public static async Task Client_DispatchesNotifications()
    {
        var clientToServer = new QueueStream();
        var serverToClient = new QueueStream();
        var client = new LspClient(serverToClient, clientToServer);
        string? notified = null;
        client.OnNotification = (method, _) => notified = method;
        var server = Task.Run(async () =>
        {
            var transport = new LspTransport(clientToServer, serverToClient);
            await transport.WriteAsync(new JsonRpcMessage(Method: "textDocument/publishDiagnostics", Params: JsonSerializer.SerializeToElement(new JsonObject())));
        });
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        var message = await client.ReceiveOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("textDocument/publishDiagnostics", message!.Method, "the notification arrives");
        Assert.Equal("textDocument/publishDiagnostics", notified, "inbound notifications reach the handler");
    }

    public static async Task Transport_MissingLengthHeaderFailsLoud()
    {
        var inbound = new QueueStream();
        var transport = new LspTransport(inbound, inbound);
        await inbound.WriteAsync(Encoding.UTF8.GetBytes("X-Custom: 1\r\n\r\nignored"));
        try
        {
            await transport.ReadAsync();
            Assert.True(false, "a message without Content-Length must fail loud");
        }
        catch (InvalidOperationException error)
        {
            Assert.True(error.Message.Contains("Content-Length"), "the error names the missing header");
        }
    }
}

/// <summary>
/// One unidirectional in-process stream: writes append to a shared buffer and wake blocked
/// readers; reads block until bytes arrive or <see cref="CloseInput"/> signals end-of-stream.
/// Sync-only — the stream base's async wrappers fall through to these methods.
/// </summary>
public sealed class QueueStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly object _gate = new();
    private bool _closed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Signal end-of-stream, waking any blocked reader to drain the remainder and see EOF.</summary>
    public void CloseInput()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            while (_buffer.Position == _buffer.Length && !_closed)
            {
                Monitor.Wait(_gate);
            }
            if (_buffer.Position == _buffer.Length) return 0;
            return _buffer.Read(buffer);
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => new(Read(buffer.Span));

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            var position = _buffer.Position;
            _buffer.Position = _buffer.Length;
            _buffer.Write(buffer);
            _buffer.Position = position;
            Monitor.PulseAll(_gate);
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
