using System.Text;

namespace Harness.Terminal;

/// <summary>
/// One live ConPTY session: the sanitized bounded scrollback, the controlled-prompt readiness
/// model for sends (<c>stdin_read</c> on prompt evidence, <c>inferred_idle</c> on silence,
/// <c>timeout</c> at the absolute bound, <c>session_exit</c> on shell exit), and the teardown
/// ladder. Exactly one send may be active per session.
/// </summary>
internal sealed class ConPtySession : ITerminalSession
{
    private readonly object _gate = new();
    private readonly ConPtyChild _child;
    private readonly ConPtyConfig _config;
    private readonly List<string> _lines = new();
    private readonly TaskCompletionSource<SubprocessTerminalOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalSanitizer _sanitizer = new();
    private readonly UTF8Encoding _utf8 = new(false, true);
    private readonly Decoder _decoder;
    private readonly List<byte> _pendingBytes = new();
    private LocalSend? _activeSend;
    private int _readLine;
    private int _closed;
    private int _promptCount;
    private bool _promptPending;
    private long _outputBytes;
    private string _motd = string.Empty;
    private int? _exitCode;
    private Task? _readLoop;

    public ConPtySession(TerminalSessionId sessionId, string? name, ConPtyChild child, ConPtyConfig config)
    {
        SessionId = sessionId;
        Name = name;
        _child = child;
        _config = config;
        _decoder = _utf8.GetDecoder();
    }

    public TerminalSessionId SessionId { get; }

    public string? Name { get; }

    public string Motd => _motd;

    public int? Pid => _child.Process?.Id;

    public Task<SubprocessTerminalOutcome> Done => _done.Task;

    /// <summary>Start the output read loop and the exit observer.</summary>
    public void Start()
    {
        _readLoop = ReadLoopAsync();
        _ = _child.Done.ContinueWith(_ =>
        {
            lock (_gate) _exitCode = _child.Process?.ExitCode;
            _done.TrySetResult(_child.Done.GetAwaiter().GetResult());
        }, TaskScheduler.Default);
    }

    public ITerminalSendOperation StartSend(TerminalSendRequest request)
    {
        lock (_gate)
        {
            if (_activeSend is { IsSettled: false } current) return current;
            var send = new LocalSend(this, request);
            _activeSend = send;
            send.Start();
            return send;
        }
    }

    public TerminalReadResult Read(TerminalReadRequest request)
    {
        lock (_gate)
        {
            var count = Math.Min(request.Count ?? 50, _lines.Count);
            if (count == 0) return new TerminalReadResult(string.Empty, _lines.Count, 0, 0, false);
            var begin = _lines.Count - count;
            return new TerminalReadResult(string.Join('\n', _lines.Skip(begin)), _lines.Count, begin, _lines.Count, _lines.Count > _config.ScrollbackLines);
        }
    }

    public TerminalSessionStatus Status()
    {
        lock (_gate)
        {
            return _done.Task.IsCompleted
                ? new TerminalSessionStatus.Exited(_exitCode, null)
                : new TerminalSessionStatus.Running();
        }
    }

    public async Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _child.Dispose();
        try
        {
            if (_readLoop is not null) await _readLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // the loop ends with the child; nothing further to observe
        }
        await _done.Task;
    }

    public TerminalSessionSnapshot Snapshot() => new(SessionId, Name, ConPtyTerminalProvider.BackendType, Pid, Status());

    /// <summary>Write one send's text (with the Enter sequence when submitting).</summary>
    internal async Task WriteAsync(string text, bool submit)
    {
        var line = submit ? text + "\r" : text;
        await _child.WriteAsync(line);
    }

    /// <summary>Consume scrollback appended since the prior read (bounding drops the oldest lines).</summary>
    internal TerminalSendRead Consume()
    {
        lock (_gate)
        {
            var pending = _lines.Skip(_readLine).ToList();
            _readLine = _lines.Count;
            return new TerminalSendRead(string.Join('\n', pending), _lines.Count > _config.ScrollbackLines);
        }
    }

    internal string Viewport()
    {
        lock (_gate) return string.Join('\n', _lines);
    }

    internal long OutputBytes
    {
        get
        {
            lock (_gate) return _outputBytes;
        }
    }

    internal bool Closed => Volatile.Read(ref _closed) != 0;

    /// <summary>Whether a new prompt marker completed since <paramref name="since"/>.</summary>
    internal bool PromptAdvanced(int since)
    {
        lock (_gate) return _promptCount > since;
    }

    internal int PromptCount
    {
        get
        {
            lock (_gate) return _promptCount;
        }
    }

    /// <summary>Resize the console (used by tests and future surface growth).</summary>
    internal void Resize(int cols, int rows) => _child.Resize(cols, rows);

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await Task.Run(() => _child.Output.Read(buffer, 0, buffer.Length));
                }
                catch (Exception)
                {
                    return; // the pipe closed with the child
                }
                if (read == 0) return;
                AppendOutput(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            // EOF or a closed pipe: the shell is gone.
            lock (_gate) _exitCode ??= _child.Process?.ExitCode;
            _done.TrySetResult(new SubprocessTerminalOutcome(_exitCode, null));
        }
    }

    private void AppendOutput(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _pendingBytes.AddRange(bytes.ToArray());
            var complete = _pendingBytes.Count - _decoder.GetCharCount(_pendingBytes.ToArray(), 0, _pendingBytes.Count, flush: false);
            // Decode the complete prefix (multibyte sequences split across reads stay pending).
            var chars = new char[_decoder.GetCharCount(_pendingBytes.ToArray(), 0, complete, flush: false)];
            var decoded = new string(chars, 0, _decoder.GetChars(_pendingBytes.ToArray(), 0, complete, chars, 0, flush: false));
            _pendingBytes.RemoveRange(0, complete);
            if (decoded.Length == 0) return;
            var sanitized = _sanitizer.Sanitize(decoded);
            AppendSanitized(sanitized);
            // A completed marker counts only once its prompt text ("hsh> ") is observed after it:
            // the marker proves the shell rendered the prompt, the text proves the render ended.
            if (_sanitizer.TakePromptMarker()) _promptPending = true;
            if (_promptPending && sanitized.Contains("hsh>", StringComparison.Ordinal))
            {
                _promptCount++;
                _promptPending = false;
            }
            if (_motd.Length == 0 && _lines.Count > 0) _motd = _lines[0];
        }
    }

    private void AppendSanitized(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            _lines.Add(trimmed);
            _outputBytes += trimmed.Length + 1;
        }
        if (_lines.Count > _config.ScrollbackLines)
        {
            _lines.RemoveRange(0, _lines.Count - _config.ScrollbackLines);
            _readLine = 0;
        }
    }

    private sealed class LocalSend : ITerminalSendOperation
    {
        private readonly ConPtySession _session;
        private readonly TaskCompletionSource<TerminalSendResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settled;

        public LocalSend(ConPtySession session, TerminalSendRequest request)
        {
            _session = session;
            Request = request;
        }

        public TerminalSendRequest Request { get; }

        public Task<TerminalSendResult> Done => _done.Task;

        public bool IsSettled => Volatile.Read(ref _settled) != 0;

        public void Start()
        {
            _ = RunAsync();
        }

        public TerminalSendRead ReadOutput() => _session.Consume();

        public bool Cancel()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            _done.TrySetResult(new TerminalSendResult(_session.Viewport(), TerminalWaitReason.Timeout, _session.Status(), _session.Viewport().Length > 0));
            return true;
        }

        private async Task RunAsync()
        {
            try
            {
                var prompts = _session.PromptCount;
                var before = _session.OutputBytes;
                await _session.WriteAsync(Request.Text, Request.Submit);
                var deadline = Environment.TickCount64 + _session._config.TimeoutMs;
                var idleDeadline = Environment.TickCount64 + _session._config.IdleSilenceMs;
                var exitTask = _session.Done;
                var reason = TerminalWaitReason.Timeout;
                while (Volatile.Read(ref _settled) == 0)
                {
                    if (_session.PromptAdvanced(prompts))
                    {
                        reason = TerminalWaitReason.StdinRead;
                        break;
                    }
                    if (exitTask.IsCompleted)
                    {
                        reason = TerminalWaitReason.SessionExit;
                        break;
                    }
                    if (Environment.TickCount64 > deadline)
                    {
                        reason = TerminalWaitReason.Timeout;
                        break;
                    }
                    if (_session.OutputBytes != before && Environment.TickCount64 > idleDeadline)
                    {
                        reason = TerminalWaitReason.InferredIdle;
                        break;
                    }
                    await Task.Delay(10);
                }
                // Drain trailing output through a short idle window.
                var idle = Task.Delay(TimeSpan.FromMilliseconds(200));
                await Task.WhenAny(exitTask, idle);
                if (Volatile.Read(ref _settled) != 0) return;
                _settled = 1;
                _done.TrySetResult(new TerminalSendResult(_session.Viewport(), reason, _session.Status(), false));
            }
            catch (Exception error)
            {
                _done.TrySetException(error);
            }
        }
    }
}

