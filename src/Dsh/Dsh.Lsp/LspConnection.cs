using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Harness.Subprocess;

namespace Harness.Lsp;

/// <summary>Static JSON helpers shared by the connection and the instance.</summary>
internal static class LspJson
{
    /// <summary>A standalone JSON <c>null</c> element (serializes as <c>null</c>, unlike a missing field).</summary>
    public static JsonElement NullElement() => JsonDocument.Parse("null").RootElement.Clone();
}

/// <summary>How to launch one server process and answer its configuration requests.</summary>
public record LspConnectionSpec(
    string Command,
    IReadOnlyList<string> Args,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    int MaxMessageBytes,
    int MaxStderrBytes,
    int KillGraceMs,
    JsonElement? Configuration);

/// <summary>Write one JSON-RPC message to the child stdin; <paramref name="done"/> reports stream settlement.</summary>
public delegate void LspConnectionWriter(Stream stdin, JsonRpcMessage message, Action<Exception?> done);

/// <summary>
/// The process-handle surface the connection needs from a spawn. The current Harness.Subprocess seam has no
/// pipe stdio modes, so this Harness.Lsp-owned surface is the §9.2 fallback: the default spawner produces
/// <see cref="LspProcessHandle"/> directly on System.Diagnostics.Process, and a future seam extension
/// (or the Wave 2 provider) adapts its handles to the same interface, keeping the connection code
/// identical either way.
/// </summary>
public interface ILspProcessHandle
{
    /// <summary>Process id (tree root); -1 when the spawn itself failed.</summary>
    int Pid { get; }

    /// <summary>Resolves at process close with exit facts; rejects only for spawn-level failures.</summary>
    Task<SubprocessOutcome> Done { get; }

    /// <summary>The piped stdin stream, framed by the connection.</summary>
    Stream Stdin { get; }

    /// <summary>The piped stdout byte stream, fed incrementally to the decoder.</summary>
    Stream Stdout { get; }

    /// <summary>The retained stderr tail (the bounded diagnostic contract).</summary>
    string StderrTail { get; }

    /// <summary>Terminate the process tree; idempotent, safe after close.</summary>
    void Terminate();

    /// <summary>Wait until the tree exits, or until <paramref name="ct"/> fires first.</summary>
    Task<bool> WaitForExitAsync(CancellationToken? ct = null);
}

/// <summary>Spawn one subprocess for this connection.</summary>
public delegate ILspProcessHandle LspConnectionSpawner(SubprocessSpawnSpec spec);

/// <summary>
/// A live JSON-RPC endpoint bound to one child process (port of <c>connection.ts</c>). Owns id
/// correlation, outbound requests/notifications, and inbound server→client requests; caps stderr,
/// surfaces framing/decoder failures as a fatal close, and exposes tree-scoped termination through the
/// handle so the instance owns teardown.
/// </summary>
public sealed class LspConnection
{
    /// <summary>The default spawner: the §9.2 fallback handle built directly on System.Diagnostics.Process.</summary>
    public static LspConnectionSpawner DefaultSpawner { get; } = LspProcessHandle.Spawn;

    /// <summary>The default writer: frame with <see cref="Framing.EncodeMessage"/> and write asynchronously.</summary>
    public static LspConnectionWriter DefaultWriter { get; } = DefaultWrite;

    private readonly ILspProcessHandle _handle;
    private readonly Stream _stdin;
    private readonly MessageDecoder _decoder;
    private readonly Func<string, JsonElement?, Task<JsonElement?>> _onServerRequest;
    private readonly LspConnectionWriter _writer;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private volatile Exception? _closeReason;

    /// <summary>
    /// Create the connection and spawn the child immediately.
    /// </summary>
    /// <param name="spec">how to launch the server and answer its config requests.</param>
    /// <param name="spawner">the process-handle seam's spawn.</param>
    /// <param name="onServerRequest">answers a server→client request; rejects to send a -32601 error response.</param>
    /// <param name="writer">message writer; tests inject callback failures without relying on OS pipe races.</param>
    public LspConnection(
        LspConnectionSpec spec,
        LspConnectionSpawner spawner,
        Func<string, JsonElement?, Task<JsonElement?>> onServerRequest,
        LspConnectionWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(onServerRequest);
        _onServerRequest = onServerRequest;
        _writer = writer ?? DefaultWriter;
        _decoder = new MessageDecoder(spec.MaxMessageBytes);
        // stdin/stdout are piped protocol streams this endpoint frames itself; stderr is a collected
        // diagnostic tail (the bounded tail IS the contract). The fallback handle interprets the spec's
        // stdio as pipe-pipe-collect regardless of the mode records (the seam lacks pipe modes).
        var argv = new List<string>(spec.Args.Count + 1) { spec.Command };
        argv.AddRange(spec.Args);
        // The seam's spawn spec carries nullable env values (null tombstones); widen the caller's
        // non-nullable map once at spawn time.
        var env = spec.Env.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        _handle = spawner(new SubprocessSpawnSpec(
            argv,
            spec.Cwd,
            new SubprocessStdio(
                new IgnoreStdin(),
                new CollectOutput(new SubprocessCollect(spec.MaxStderrBytes)),
                new CollectOutput(new SubprocessCollect(spec.MaxStderrBytes))),
            spec.KillGraceMs,
            Env: env));
        if (_handle.Stdin is null || _handle.Stdout is null)
        {
            throw new InvalidOperationException("lsp-stdio: subprocess implementation dropped a piped protocol stream");
        }
        _stdin = _handle.Stdin;
        _ = ObserveCloseAsync();
        _ = PumpStdoutAsync();
    }

    /// <summary>The child's pid, or -1 when the spawn produced no pid (so signalling is a no-op).</summary>
    public int Pid => _handle.Pid;

    /// <summary>The retained stderr tail, for diagnostics on a failed server.</summary>
    public string StderrTail => _handle.StderrTail;

    /// <summary>Whether the transport has failed even if the child close event has not arrived yet.</summary>
    public bool Failed => _closeReason is not null;

    /// <summary>True only when this connection produced that exact failure (identity, like the TS check).</summary>
    public bool FailedWith(Exception error) => ReferenceEquals(_closeReason, error);

    /// <summary>Resolves exactly once the child process has fully exited (or the spawn failed).</summary>
    public Task Closed => _closed.Task;

    /// <summary>Send a request and await its result; rejects on an error response, write failure, or close.</summary>
    public Task<JsonElement?> Request(string method, JsonElement? parameters)
    {
        if (_closeReason is not null) return Task.FromException<JsonElement?>(_closeReason);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = completion;
            // Close may have raced between the fast-path check and the reservation; the entry must not
            // outlive the connection (FailAll already ran with an empty map).
            if (_closeReason is not null)
            {
                _pending.Remove(id);
                return Task.FromException<JsonElement?>(_closeReason);
            }
        }
        // write() records either synchronous or callback-delivered failures on the connection and rejects
        // every pending request; this handler only consumes the write promise itself.
        _ = WriteMessageAsync(new JsonRpcMessage(Id: id, Method: method, Params: parameters)).ContinueWith(_ => { }, TaskScheduler.Default);
        // A caller that stops awaiting can leave this promise to reject later; the no-op continuation
        // keeps that from surfacing as an unobserved fault. The returned promise still delivers the
        // rejection to the caller's own await/catch.
        _ = completion.Task.ContinueWith(_ => { }, TaskScheduler.Default);
        return completion.Task;
    }

    /// <summary>Send a notification (no id, no response); resolves when the framed message has been written.</summary>
    public Task Notify(string method, JsonElement? parameters)
        => WriteMessageAsync(new JsonRpcMessage(Method: method, Params: parameters));

    /// <summary>Send a best-effort <c>$/cancelRequest</c> for an in-flight request id; write failures are ignored.</summary>
    public void Cancel(long requestId)
    {
        _ = WriteMessageAsync(new JsonRpcMessage(Method: "$/cancelRequest", Params: JsonSerializer.SerializeToElement(new { id = requestId })))
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>The id the NEXT request() will use, so the instance can pre-arm a cancel.</summary>
    public long PeekNextId() => Interlocked.Read(ref _nextId) + 1;

    /// <summary>Terminate the server's process tree; idempotent, safe after close.</summary>
    public void Terminate() => _handle.Terminate();

    /// <summary>Wait until the owned process tree has exited, or until <paramref name="ct"/> fires first.</summary>
    public Task<bool> WaitForProcessTreeExit(CancellationToken? ct = null) => _handle.WaitForExitAsync(ct);

    private async Task ObserveCloseAsync()
    {
        try
        {
            await _handle.Done;
        }
        catch (Exception error)
        {
            // A spawn-level failure never produces a close event; the rejection is the fatal cause and
            // the close boundary at once.
            Fail(error);
        }
        Close();
    }

    /// <summary>Record the close reason, reject every pending request, and resolve <see cref="Closed"/>.</summary>
    private void Close()
    {
        var reason = _closeReason ?? new InvalidOperationException(ExitMessage());
        _closeReason = reason;
        FailAll(reason);
        _closed.TrySetResult();
    }

    private async Task PumpStdoutAsync()
    {
        var buffer = new byte[16384];
        while (true)
        {
            int read;
            try
            {
                read = await _handle.Stdout.ReadAsync(buffer);
            }
            catch (IOException)
            {
                return; // pipe closed on teardown
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (read == 0) return;
            try
            {
                OnStdout(buffer.AsMemory(0, read));
            }
            catch (Exception error)
            {
                Fail(error);
                return;
            }
        }
    }

    private void OnStdout(ReadOnlyMemory<byte> chunk)
    {
        JsonRpcMessage[] messages;
        try
        {
            messages = _decoder.Push(chunk);
        }
        catch (Exception error)
        {
            // A framing/JSON failure corrupts the stream position irrecoverably: fail the instance and
            // terminate the whole group so helper processes don't outlive the leader.
            Fail(error);
            _handle.Terminate();
            return;
        }
        foreach (var message in messages) Dispatch(message);
    }

    private void Dispatch(JsonRpcMessage message)
    {
        if (message.Method is not null && message.Id is not null)
        {
            _ = HandleServerRequestAsync(message.Id.Value, message.Method, message.Params);
            return;
        }
        if (message.Method is not null)
        {
            // A server→client notification (for example diagnostics, logs): dropped without a reply.
            return;
        }
        if (message.Id is { } id) HandleResponse(id, message);
    }

    private async Task HandleServerRequestAsync(long id, string method, JsonElement? parameters)
    {
        try
        {
            var result = await _onServerRequest(method, parameters);
            // A null result must still appear on the wire ("result":null), so encode a JSON null element.
            await WriteMessageAsync(new JsonRpcMessage(Id: id, Result: result ?? LspJson.NullElement()));
        }
        catch (Exception error)
        {
            try
            {
                await WriteMessageAsync(new JsonRpcMessage(Id: id, Error: JsonSerializer.SerializeToElement(new { code = -32601, message = error.Message })));
            }
            catch
            {
                // The connection already failed; the -32601 reply is best-effort.
            }
        }
    }

    private void HandleResponse(long id, JsonRpcMessage message)
    {
        TaskCompletionSource<JsonElement?>? pending;
        lock (_pending)
        {
            if (!_pending.Remove(id, out pending)) return; // unknown id: dropped silently
        }
        var error = message.Error;
        if (error.HasValue && error.Value.ValueKind == JsonValueKind.Object)
        {
            var text = error.Value.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            pending.TrySetException(new InvalidOperationException(text ?? "LSP error response"));
            return;
        }
        pending.TrySetResult(message.Result);
    }

    private Task WriteMessageAsync(JsonRpcMessage message)
    {
        if (_closeReason is not null) return Task.FromException(_closeReason);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(Exception? error)
        {
            if (error is null)
            {
                completion.TrySetResult();
                return;
            }
            Fail(error);
            completion.TrySetException(error);
        }
        try
        {
            _writer(_stdin, message, Done);
        }
        catch (Exception error)
        {
            Fail(error);
            completion.TrySetException(error);
        }
        return completion.Task;
    }

    /// <summary>The exit-close error message, appending the retained stderr tail when the server wrote any.</summary>
    private string ExitMessage()
    {
        var tail = _handle.StderrTail.Trim();
        return tail.Length == 0 ? "language server exited" : $"language server exited; stderr: {tail}";
    }

    private void Fail(Exception error)
    {
        if (_closeReason is null) _closeReason = error;
        FailAll(error);
    }

    private void FailAll(Exception error)
    {
        TaskCompletionSource<JsonElement?>[] waiting;
        lock (_pending)
        {
            waiting = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var pending in waiting) pending.TrySetException(error);
    }

    private static void DefaultWrite(Stream stdin, JsonRpcMessage message, Action<Exception?> done)
    {
        _ = WriteFramedAsync(stdin, message, done);
    }

    private static async Task WriteFramedAsync(Stream stdin, JsonRpcMessage message, Action<Exception?> done)
    {
        try
        {
            var bytes = Framing.EncodeMessage(message);
            // Asynchronous so a full pipe suspends without blocking the caller's thread; the done
            // callback fires once the OS accepts the bytes (or the write fails).
            await stdin.WriteAsync(bytes);
            done(null);
        }
        catch (Exception error)
        {
            done(error);
        }
    }
}

/// <summary>
/// The §9.2 fallback process handle: spawns via System.Diagnostics.Process with all three stdio
/// redirected, scrubs the ambient DSH_* environment before the spec's entries merge, retains a bounded
/// UTF-8-safe stderr tail, and tree-terminates via Harness.Subprocess.ProcessTree.Kill.
/// </summary>
internal sealed class LspProcessHandle : ILspProcessHandle
{
    private readonly Process _process;
    private readonly BoundedByteTail _stderrTail;
    private readonly TaskCompletionSource<SubprocessOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _terminated;

    private LspProcessHandle(Process process, int maxStderrBytes)
    {
        _process = process;
        Pid = process.Id;
        Stdin = process.StandardInput.BaseStream;
        Stdout = process.StandardOutput.BaseStream;
        _stderrTail = new BoundedByteTail(maxStderrBytes);
        _ = PumpStderrAsync();
    }

    /// <summary>Build the spawn request into a live handle; a spawn failure yields a failed handle whose <see cref="Done"/> rejects.</summary>
    public static ILspProcessHandle Spawn(SubprocessSpawnSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var info = new ProcessStartInfo
        {
            FileName = spec.Argv[0],
            WorkingDirectory = spec.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Argv.Skip(1)) info.ArgumentList.Add(argument);
        BuildEnvironment(info, spec.Env);
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var maxStderrBytes = (spec.Stdio.Stderr as CollectOutput)?.Collect.MaxBytes ?? 1_000_000;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            process.Dispose();
            return new FailedHandle(error, spec.Argv[0]);
        }
        return new LspProcessHandle(process, maxStderrBytes);
    }

    public int Pid { get; }

    public Task<SubprocessOutcome> Done => _done.Task;

    public Stream Stdin { get; }

    public Stream Stdout { get; }

    public string StderrTail => _stderrTail.Text;

    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        ProcessTree.Kill(_process);
    }

    public async Task<bool> WaitForExitAsync(CancellationToken? ct = null)
    {
        try
        {
            await _process.WaitForExitAsync(ct ?? CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true; // already exited and disposed
        }
        catch (InvalidOperationException)
        {
            return true; // already exited and disposed
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var buffer = new byte[8192];
            var stderr = _process.StandardError.BaseStream;
            while (true)
            {
                var read = await stderr.ReadAsync(buffer);
                if (read == 0) break;
                _stderrTail.Append(buffer.AsSpan(0, read));
            }
        }
        catch (IOException)
        {
            // A killed process closes the pipe mid-read; treat it as end-of-stream.
        }
        try
        {
            await _process.WaitForExitAsync();
            _done.TrySetResult(new SubprocessOutcome(_process.ExitCode, null));
        }
        catch (Exception error)
        {
            _done.TrySetException(error);
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>Scrub the ambient DSH_* facts, then merge the spec's explicit entries (null tombstones remove).</summary>
    private static void BuildEnvironment(ProcessStartInfo info, IReadOnlyDictionary<string, string?>? env)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith(DshEnv.Prefix, StringComparison.Ordinal))
            {
                // The start-info environment inherits the parent's; a managed fact must be explicitly
                // removed, not merely skipped.
                info.Environment.Remove(key);
                continue;
            }
            info.Environment[key] = (string?)entry.Value;
        }
        if (env is null) return;
        foreach (var (key, value) in env)
        {
            if (value is null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }
    }

    /// <summary>A spawn that failed before producing a process: rejects <see cref="Done"/> so pending requests fail.</summary>
    private sealed class FailedHandle : ILspProcessHandle
    {
        public FailedHandle(Exception error, string command)
        {
            Done = Task.FromException<SubprocessOutcome>(
                new InvalidOperationException($"subprocess: failed to spawn \"{command}\": {error.Message}", error));
        }

        public int Pid => -1;

        public Task<SubprocessOutcome> Done { get; }

        public Stream Stdin => Stream.Null;

        public Stream Stdout => Stream.Null;

        public string StderrTail => string.Empty;

        public void Terminate()
        {
        }

        public Task<bool> WaitForExitAsync(CancellationToken? ct = null) => Task.FromResult(false);
    }
}

/// <summary>A byte-bounded retained tail that never splits a UTF-8 character at either end.</summary>
internal sealed class BoundedByteTail
{
    private readonly object _gate = new();
    private readonly int _maxBytes;
    private byte[] _buffer = new byte[256];
    private int _count;

    public BoundedByteTail(int maxBytes)
    {
        _maxBytes = maxBytes;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_count + bytes.Length > _buffer.Length)
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _count + bytes.Length)];
                _buffer.AsSpan(0, _count).CopyTo(grown);
                _buffer = grown;
            }
            bytes.CopyTo(_buffer.AsSpan(_count));
            _count += bytes.Length;
            while (_count > _maxBytes) DropFirstCharacter();
        }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                var end = _count;
                // The tail may end mid-character (a chunk boundary); never decode a partial sequence.
                while (end > 0 && IsContinuation(_buffer[end - 1])) end--;
                if (end > 0)
                {
                    var needed = SequenceLength(_buffer[end - 1]);
                    var sequenceEnd = end - 1 + needed;
                    if (sequenceEnd > _count)
                    {
                        // The leading byte starts a sequence that extends past the buffer: drop it.
                        end--;
                    }
                    else
                    {
                        end = sequenceEnd;
                    }
                }
                return Encoding.UTF8.GetString(_buffer, 0, end);
            }
        }
    }

    private void DropFirstCharacter()
    {
        var needed = Math.Min(SequenceLength(_buffer[0]), _count);
        Buffer.BlockCopy(_buffer, needed, _buffer, 0, _count - needed);
        _count -= needed;
    }

    private static bool IsContinuation(byte value) => (value & 0xC0) == 0x80;

    private static int SequenceLength(byte lead)
    {
        if (lead < 0x80) return 1;
        if ((lead & 0xE0) == 0xC0) return 2;
        if ((lead & 0xF0) == 0xE0) return 3;
        if ((lead & 0xF8) == 0xF0) return 4;
        return 1; // invalid lead byte: treat as one replacement character
    }
}
