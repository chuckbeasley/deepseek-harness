using System.Text;
using System.Text.Json;

namespace Dsh.Lsp;

/// <summary>A JSON-RPC 2.0 message envelope shared by the LSP client and server sides. The version field is named <c>Jsonrpc</c> so camelCase serialization emits the protocol's exact <c>jsonrpc</c> key.</summary>
public sealed record JsonRpcMessage(string? Jsonrpc = "2.0", string? Method = null, JsonElement? Params = null, long? Id = null, JsonElement? Result = null, JsonElement? Error = null);

/// <summary>
/// Minimal LSP stdio transport (port of the lsp-stdio framing): Content-Length-prefixed JSON-RPC
/// messages over a duplex stream pair. In-memory dispatch for tests; the process-spawn host lives in
/// <see cref="LspConnection"/>.
/// </summary>
public sealed class LspTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LspTransport(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>Read the next message (blocking); returns null at end of stream.</summary>
    public async Task<JsonRpcMessage?> ReadAsync(CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line is null) throw new EndOfStreamException("lsp: stream ended inside message headers");
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon < 0) throw new InvalidOperationException($"lsp: malformed header line \"{line}\"");
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (!headers.TryGetValue("Content-Length", out var lengthText)
            || !int.TryParse(lengthText, out var length)
            || length < 0)
        {
            throw new InvalidOperationException("lsp: missing or invalid Content-Length header");
        }
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = await _input.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (chunk == 0) throw new EndOfStreamException("lsp: stream ended inside a message body");
            read += chunk;
        }
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
        return Deserialize(document.RootElement);
    }

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Write one message with its Content-Length header.</summary>
    public async Task WriteAsync(JsonRpcMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, WireJson);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeGate.WaitAsync(ct);
        try
        {
            await _output.WriteAsync(header, ct);
            await _output.WriteAsync(body, ct);
            await _output.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var builder = new StringBuilder();
        var single = new byte[1];
        while (true)
        {
            var read = await _input.ReadAsync(single, ct);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }
            if (single[0] == '\n') return builder.ToString().TrimEnd('\r');
            builder.Append((char)single[0]);
        }
    }

    /// <summary>Parse one JSON-RPC envelope from a JSON element (shared by the transport and the stream decoder).</summary>
    public static JsonRpcMessage Deserialize(JsonElement root)
    {
        // A framed non-object (JSON number/null/string) is not a dispatchable message; the connection
        // ignores it. Return the empty envelope so parsing never throws on untrusted frames.
        if (root.ValueKind != JsonValueKind.Object) return new JsonRpcMessage();
        var jsonrpc = TryGet(root, "jsonrpc", out var version) ? version.GetString() : null;
        var method = TryGet(root, "method", out var methodValue) ? methodValue.GetString() : null;
        JsonElement? parameters = TryGet(root, "params", out var paramsValue) ? paramsValue.Clone() : null;
        long? id = TryGet(root, "id", out var idValue) && idValue.ValueKind == JsonValueKind.Number ? idValue.GetInt64() : null;
        JsonElement? result = TryGet(root, "result", out var resultValue) ? resultValue.Clone() : null;
        JsonElement? error = TryGet(root, "error", out var errorValue) ? errorValue.Clone() : null;
        return new JsonRpcMessage(jsonrpc, method, parameters, id, result, error);
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

/// <summary>
/// A minimal LSP client over a transport: send requests and notifications, dispatch inbound
/// notifications through a handler. Used by tests against in-memory duplex streams; the
/// process-spawn host arrives with a later wave.
/// </summary>
public sealed class LspClient
{
    private readonly LspTransport _transport;
    private readonly Dictionary<long, TaskCompletionSource<JsonRpcMessage>> _pending = new();
    private long _nextId;
    private bool _started;

    public LspClient(Stream input, Stream output)
    {
        _transport = new LspTransport(input, output);
    }

    /// <summary>Handler for inbound server notifications (method, params).</summary>
    public Action<string, JsonElement?>? OnNotification { get; set; }

    /// <summary>Start the read loop (idempotent).</summary>
    public void Start(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;
        _ = ReadLoopAsync(ct);
    }

    /// <summary>Send a request and await its response (a timeout is the caller's).</summary>
    public async Task<JsonRpcMessage> RequestAsync(string method, JsonElement? parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = completion;
        await _transport.WriteAsync(new JsonRpcMessage(Method: method, Params: parameters, Id: id), ct);
        return await completion.Task.WaitAsync(ct);
    }

    /// <summary>Send a notification (no response expected).</summary>
    public Task NotifyAsync(string method, JsonElement? parameters, CancellationToken ct = default)
        => _transport.WriteAsync(new JsonRpcMessage(Method: method, Params: parameters), ct);

    /// <summary>
    /// Read and dispatch ONE inbound message deterministically: a response completes its pending
    /// request, a notification reaches <see cref="OnNotification"/>. The message is also returned.
    /// </summary>
    public async Task<JsonRpcMessage?> ReceiveOnceAsync(CancellationToken ct = default)
    {
        var message = await _transport.ReadAsync(ct);
        if (message is null) return null;
        if (message.Id is { } id && (message.Result is not null || message.Error is not null))
        {
            TaskCompletionSource<JsonRpcMessage>? completion;
            lock (_pending)
            {
                if (_pending.Remove(id, out completion))
                {
                }
            }
            completion?.TrySetResult(message);
        }
        else
        {
            OnNotification?.Invoke(message.Method ?? string.Empty, message.Params);
        }
        return message;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var message = await _transport.ReadAsync(ct);
                if (message is null) return;
                if (message.Id is { } id && (message.Result is not null || message.Error is not null))
                {
                    TaskCompletionSource<JsonRpcMessage>? completion;
                    lock (_pending)
                    {
                        if (_pending.Remove(id, out completion))
                        {
                        }
                    }
                    completion?.TrySetResult(message);
                }
                else
                {
                    OnNotification?.Invoke(message.Method ?? string.Empty, message.Params);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The caller's teardown owns cancellation.
        }
        catch (Exception error)
        {
            // A crashed read loop must never silently drop pending responses: fail every waiter.
            Console.Error.WriteLine($"lsp client read loop failed: {error.Message}");
            lock (_pending)
            {
                foreach (var (_, completion) in _pending) completion.TrySetException(error);
                _pending.Clear();
            }
        }
    }
}
