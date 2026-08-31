using System.Text;
using System.Text.Json;

namespace Dsh.Sdk.Protocol;

/// <summary>A JSON-RPC error response, preserving the wire <c>code</c> and optional <c>data</c>.</summary>
public sealed class JsonRpcResponseError : Exception
{
    /// <summary>Create the wire error.</summary>
    /// <param name="code">the wire error code, or <c>null</c> when the peer sent none.</param>
    /// <param name="message">the wire error message.</param>
    /// <param name="data">the optional structured error payload, verbatim.</param>
    public JsonRpcResponseError(int? code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        Data = data;
    }

    /// <summary>The wire error code.</summary>
    public int? Code { get; }

    /// <summary>The optional structured error payload.</summary>
    public JsonElement? Data { get; }
}

/// <summary>The outbound request and notification surface shared by the runtime server and SDK clients.</summary>
public interface IJsonRpcPeer
{
    /// <summary>Send a request and await its response.</summary>
    /// <param name="method">the JSON-RPC method name.</param>
    /// <param name="paramsValue">the request parameters object; omitted when <c>null</c>.</param>
    /// <param name="cancellationToken">abandonment: removes the pending entry and rejects the request.</param>
    /// <returns>the result; throws <see cref="JsonRpcResponseError"/> on an error response, an
    /// <see cref="IOException"/> on a write failure or closure, and
    /// <see cref="OperationCanceledException"/> on cancellation.</returns>
    Task<JsonElement?> RequestAsync(string method, object? paramsValue = null, CancellationToken cancellationToken = default);

    /// <summary>Send a notification; omitted params produce no <c>params</c> member.</summary>
    void Notify(string method, object? paramsValue = null);
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 over caller-owned streams (port of the TS
/// <c>JsonRpcLineTransport</c>): frames with <c>id</c> and <c>method</c> are requests, <c>id</c>
/// alone is a response, and <c>method</c> alone is a notification. Malformed lines are ignored;
/// a missing request handler answers <c>-32601</c>, a handler failure <c>-32603</c>, and a
/// notification without a handler is dropped. <see cref="Start"/> attaches the reader;
/// <see cref="Close"/> detaches it and rejects pending requests without disposing the streams.
/// Writes serialize under one gate and flush per frame, so a concurrent request cannot interleave
/// with a notification (documented: the TS relies on the event loop).
/// </summary>
public sealed class JsonRpcLineTransport : IJsonRpcPeer
{
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;

    private readonly Stream _input;
    private readonly StreamWriter _writer;
    private readonly object _writeGate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement?>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _reader;
    private Func<string, JsonElement?, Task<JsonElement?>>? _requestHandler;
    private Action<string, JsonElement?>? _notificationHandler;
    private bool _closed;

    /// <summary>Create the transport over caller-owned streams.</summary>
    public JsonRpcLineTransport(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _writer = new StreamWriter(output ?? throw new ArgumentNullException(nameof(output)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            NewLine = "\n",
        };
    }

    /// <summary>Attach the input reader and begin processing frames. Idempotent.</summary>
    public void Start()
    {
        lock (_pending)
        {
            if (_reader is not null) return;
            _reader = Task.Run(() => ReadLoopAsync(_readerCts.Token));
        }
    }

    /// <summary>Detach the reader and reject pending requests. Safe before <see cref="Start"/>.</summary>
    public void Close()
    {
        lock (_pending)
        {
            if (_closed) return;
            _closed = true;
            _readerCts.Cancel();
            FailPending(new IOException("JSON-RPC transport closed"));
        }
    }

    /// <summary>Install the request handler, replacing any prior handler; a rejection becomes a <c>-32603</c> error response.</summary>
    public void OnRequest(Func<string, JsonElement?, Task<JsonElement?>> handler)
    {
        _requestHandler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>Install the notification handler, replacing any prior handler.</summary>
    public void OnNotification(Action<string, JsonElement?> handler)
    {
        _notificationHandler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public Task<JsonElement?> RequestAsync(string method, object? paramsValue = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var id = "req_" + Guid.NewGuid().ToString("N");
        var message = paramsValue is null
            ? JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, method })
            : JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, method, @params = paramsValue });
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            if (_closed) return Task.FromException<JsonElement?>(new IOException("JSON-RPC transport closed"));
            _pending[id] = tcs;
        }
        // The registration must outlive this method: it detaches only when the request settles,
        // so a cancel after the send still fails the pending request (the TS removes its abort
        // listener in the resolve/reject paths, not at send time).
        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() =>
            {
                lock (_pending) _pending.Remove(id);
                tcs.TrySetException(new OperationCanceledException("JSON-RPC request aborted", cancellationToken));
            })
            : default;
        try
        {
            WriteLine(message);
        }
        catch (Exception error)
        {
            lock (_pending) _pending.Remove(id);
            tcs.TrySetException(error);
        }
        return AwaitSettledAsync(tcs, registration);
    }

    private static async Task<JsonElement?> AwaitSettledAsync(
        TaskCompletionSource<JsonElement?> tcs, CancellationTokenRegistration registration)
    {
        try
        {
            return await tcs.Task;
        }
        finally
        {
            registration.Dispose();
        }
    }

    /// <inheritdoc />
    public void Notify(string method, object? paramsValue = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var message = paramsValue is null
            ? JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", method })
            : JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", method, @params = paramsValue });
        WriteLine(message);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(_input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                await HandleLineAsync(trimmed);
            }
        }
        catch (OperationCanceledException)
        {
            // Close() cancelled the reader; the pending entries were already rejected there.
        }
        catch (Exception)
        {
            // A failed input stream fails every pending request below.
        }
        finally
        {
            lock (_pending)
            {
                if (!_closed)
                {
                    _closed = true;
                    FailPending(new IOException("JSON-RPC input closed"));
                }
            }
        }
    }

    private async Task HandleLineAsync(string line)
    {
        JsonElement frame;
        try
        {
            frame = JsonDocument.Parse(line).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Only JSON syntax errors reach this catch; malformed peer lines are ignored.
            return;
        }
        if (frame.ValueKind != JsonValueKind.Object) return;
        var hasId = frame.TryGetProperty("id", out var id)
            && (id.ValueKind == JsonValueKind.String || id.ValueKind == JsonValueKind.Number);
        var hasMethod = frame.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
        if (hasId && hasMethod)
        {
            await HandleIncomingRequestAsync(id, method.GetString()!, ParamsOf(frame));
            return;
        }
        if (hasId)
        {
            HandleIncomingResponse(id, frame);
            return;
        }
        if (hasMethod)
        {
            _notificationHandler?.Invoke(method.GetString()!, ParamsOf(frame));
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        var handler = _requestHandler;
        if (handler is null)
        {
            WriteError(id, MethodNotFound, $"method not found: {method}");
            return;
        }
        try
        {
            var result = await handler(method, parameters);
            WriteLine(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, result }));
        }
        catch (Exception error)
        {
            WriteError(id, InternalError, error.Message);
        }
    }

    private void HandleIncomingResponse(JsonElement id, JsonElement frame)
    {
        var idKey = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
        TaskCompletionSource<JsonElement?>? pending;
        lock (_pending)
        {
            if (!_pending.Remove(idKey, out pending)) return;
        }
        if (frame.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.Number
                ? codeValue.GetInt32()
                : (int?)null;
            var message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString()!
                : "JSON-RPC error";
            var data = error.TryGetProperty("data", out var dataValue) ? dataValue.Clone() : (JsonElement?)null;
            pending.TrySetException(new JsonRpcResponseError(code, message, data));
            return;
        }
        pending.TrySetResult(frame.TryGetProperty("result", out var result) ? result.Clone() : null);
    }

    private void WriteError(JsonElement id, int code, string message)
        => WriteLine(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, error = new { code, message } }));

    private void WriteLine(JsonElement message)
    {
        lock (_writeGate)
        {
            if (_closed) throw new IOException("JSON-RPC transport closed");
            _writer.Write(message.GetRawText());
            _writer.Write('\n');
            _writer.Flush();
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending.Values) pending.TrySetException(error);
        _pending.Clear();
    }

    /// <summary>Normalize JSON-RPC <c>params</c> to an object (absent or non-object params collapse to <c>null</c>).</summary>
    private static JsonElement? ParamsOf(JsonElement frame)
        => frame.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
            ? parameters.Clone()
            : null;
}

