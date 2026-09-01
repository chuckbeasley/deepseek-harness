using System.Diagnostics;
using System.Text;
using Harness.Cordis.Core;

namespace Harness.Terminal;

/// <summary>
/// Local terminal provider (ctx.terminal; backend type "local"). Port of the terminal-bash
/// backend with a documented deviation: the child runs with REDIRECTED stdio (line-buffered,
/// no TTY semantics) instead of a PTY — the ConPTY/pty backend arrives with the native bridge
/// wave. Sends append the Enter sequence when <see cref="TerminalSendRequest.Submit"/> is set;
/// output is retained in a bounded line scrollback with consuming incremental reads.
/// </summary>
public sealed class LocalTerminalProvider : Service, ITerminalService
{
    /// <summary>The backend type this provider registers.</summary>
    public const string BackendType = "local";

    private readonly object _gate = new();
    private readonly List<LocalSession> _sessions = new();
    private int _counter;

    /// <summary>Create the provider and register it as <c>terminal</c>.</summary>
    public LocalTerminalProvider(Context ctx)
        : base(ctx, "terminal")
    {
    }

    /// <inheritdoc />
    public Task<ITerminalSession> OpenAsync(TerminalOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // "shell" is the recorded corpus's backend type for this provider (the TS terminal-bash
        // spelling); "local" remains the port's own alias.
        if (request.Type != BackendType && request.Type != "shell")
        {
            throw new InvalidOperationException($"terminal: unknown backend type {request.Type} (registered: {BackendType})");
        }
        var shell = Environment.GetEnvironmentVariable("HSH_SHELL_PATH")
            ?? (OperatingSystem.IsWindows() ? "cmd.exe" : "sh");
        var info = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(request.Cwd) ? Environment.CurrentDirectory : request.Cwd,
        };
        if (shell.EndsWith("sh.exe", StringComparison.OrdinalIgnoreCase) || shell.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            // A quiet POSIX shell with the recorded "hsh> " prompt: the initial output line the
            // read surface returns as the motd.
            info.ArgumentList.Add("--noprofile");
            info.ArgumentList.Add("--norc");
            info.Environment["PS1"] = "hsh> ";
        }
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"terminal: failed to spawn {shell}: {error.Message}", error);
        }
        int id;
        lock (_gate) id = ++_counter;
        var session = new LocalSession(new TerminalSessionId($"pty-{id}"), request.Name, process);
        lock (_gate) _sessions.Add(session);
        _ = session.Done.ContinueWith(_ =>
        {
            lock (_gate) _sessions.Remove(session);
        }, TaskScheduler.Default);
        return Task.FromResult<ITerminalSession>(session);
    }

    /// <inheritdoc />
    public IReadOnlyList<TerminalSessionSnapshot> List()
    {
        lock (_gate) return _sessions.Select(session => session.Snapshot()).ToArray();
    }

    /// <summary>Teardown: close and await every live session.</summary>
    public override async ValueTask StopAsync()
    {
        LocalSession[] live;
        lock (_gate) live = _sessions.ToArray();
        foreach (var session in live)
        {
            await session.CloseAsync("terminal service disposed");
        }
        await base.StopAsync();
    }

    /// <summary>One live redirected-stdio session: bounded scrollback, consuming sends, exit settlement.</summary>
    private sealed class LocalSession : ITerminalSession
    {
        private const int MaxLines = 500;

        /// <summary>The seeded prompt line that forms the session's motd.</summary>
        private const string PromptLine = "hsh> ";

        private readonly object _gate = new();
        private readonly Process _process;
        private readonly List<string> _lines = new();
        private readonly TaskCompletionSource<SubprocessTerminalOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LocalSend? _activeSend;
        private int _readLine;
        private int _closed;
        private string _motd = string.Empty;

        public LocalSession(TerminalSessionId sessionId, string? name, Process process)
        {
            SessionId = sessionId;
            Name = name;
            _process = process;
            Pid = process.Id;
            // The recorded shell backend's motd is the "hsh> " prompt line; it is seeded as the
            // first scrollback line so the read surface is deterministic before any child output.
            lock (_gate)
            {
                _lines.Add(PromptLine);
                _motd = PromptLine;
            }
            _ = RunAsync();
        }

        public TerminalSessionId SessionId { get; }

        public string? Name { get; }

        public string Motd => _motd;

        public int? Pid { get; }

        public Task<SubprocessTerminalOutcome> Done => _done.Task;

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
                return new TerminalReadResult(string.Join('\n', _lines.Skip(begin)), _lines.Count, begin, _lines.Count, _lines.Count > MaxLines);
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
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // tree gone
            }
            await _done.Task;
        }

        public TerminalSessionSnapshot Snapshot() => new(SessionId, Name, BackendType, Pid, Status());

        /// <summary>Write one send's text to the child stdin (with the Enter sequence when submitting).</summary>
        internal async Task WriteAsync(string text, bool submit)
        {
            var line = submit ? text + Environment.NewLine : text;
            await _process.StandardInput.WriteAsync(line);
            await _process.StandardInput.FlushAsync();
        }

        /// <summary>Consume scrollback appended since the prior read (bounding drops the oldest lines).</summary>
        internal TerminalSendRead Consume()
        {
            lock (_gate)
            {
                var pending = _lines.Skip(_readLine).ToList();
                _readLine = _lines.Count;
                return new TerminalSendRead(string.Join('\n', pending), _lines.Count > MaxLines);
            }
        }

        internal string Viewport()
        {
            lock (_gate) return string.Join('\n', _lines);
        }

        internal bool Closed => Volatile.Read(ref _closed) != 0;

        /// <summary>Bytes of scrollback appended since the session opened (settlement polling).</summary>
        internal long OutputBytes
        {
            get
            {
                lock (_gate) return _outputBytes;
            }
        }

        private long _outputBytes;

        private int? _exitCode;

        private async Task RunAsync()
        {
            var readOut = Task.Run(ReadLoop);
            await _process.WaitForExitAsync();
            _exitCode = _process.ExitCode;
            await readOut;
            _done.TrySetResult(new SubprocessTerminalOutcome(_process.ExitCode, null));
            _process.Dispose();
        }

        private async Task ReadLoop()
        {
            var buffer = new char[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await _process.StandardOutput.ReadAsync(buffer.AsMemory());
                }
                catch (InvalidOperationException)
                {
                    return; // stream closed with the process
                }
                if (read == 0) return;
                var text = new string(buffer, 0, read);
                lock (_gate)
                {
                    foreach (var line in text.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (trimmed.Length == 0) continue;
                        _lines.Add(trimmed);
                        _outputBytes += trimmed.Length + 1;
                    }
                    if (_lines.Count > MaxLines)
                    {
                        _lines.RemoveRange(0, _lines.Count - MaxLines);
                        _readLine = 0;
                    }
                    if (_motd.Length == 0 && _lines.Count > 0) _motd = _lines[0];
                }
            }
        }

        private sealed class LocalSend : ITerminalSendOperation
        {
            private readonly LocalSession _session;
            private readonly TaskCompletionSource<TerminalSendResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _settled;

            public LocalSend(LocalSession session, TerminalSendRequest request)
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
                    var before = _session.OutputBytes;
                    await _session.WriteAsync(Request.Text, Request.Submit);
                    // First wait for the command's output (or session exit), bounded by a long
                    // window so a slow spawn under load cannot settle the send before the echo.
                    var exitTask = _session.Done;
                    var deadline = Environment.TickCount64 + 15_000;
                    while (Volatile.Read(ref _settled) == 0 && !exitTask.IsCompleted && _session.OutputBytes == before)
                    {
                        if (Environment.TickCount64 > deadline) break;
                        await Task.Delay(50);
                    }
                    if (Volatile.Read(ref _settled) != 0) return;
                    var reason = exitTask.IsCompleted ? TerminalWaitReason.SessionExit : TerminalWaitReason.Timeout;
                    // Then let trailing output drain through a short idle window.
                    var idle = Task.Delay(TimeSpan.FromMilliseconds(300));
                    await Task.WhenAny(exitTask, idle);
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
}

/// <summary>Exit facts of the terminal's top-level process.</summary>
public sealed record SubprocessTerminalOutcome(int? ExitCode, string? Signal);
