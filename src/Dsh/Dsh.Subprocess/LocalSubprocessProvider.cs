using System.Diagnostics;
using System.Text;
using Harness.Cordis.Core;

namespace Harness.Subprocess;

/// <summary>
/// Local process-tree provider (ctx.subprocess; port of the subprocess-local executor minus the
/// raw piped-stream and terminal-session primitives, which arrive with the terminal seam). Output
/// collection is bounded in-memory with a retained TAIL and an optional full-stream spill file;
/// termination is tree-scoped. The ambient environment is scrubbed of <c>DSH_*</c> facts before
/// the spec's explicit entries merge, so a caller entry can never inherit a stale managed value.
/// </summary>
public sealed class LocalSubprocessProvider : Service, ISubprocessService
{
    private readonly object _gate = new();
    private readonly List<LocalHandle> _live = new();

    /// <summary>Create the provider and register it as <c>subprocess</c>.</summary>
    public LocalSubprocessProvider(Context ctx)
        : base(ctx, "subprocess")
    {
    }

    /// <inheritdoc />
    public ISubprocessHandle Spawn(SubprocessSpawnSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Argv.Count == 0 || spec.Argv[0].Length == 0)
        {
            throw new ArgumentException("subprocess: argv must start with the program", nameof(spec));
        }
        var info = new ProcessStartInfo
        {
            FileName = spec.Argv[0],
            WorkingDirectory = spec.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        AppendArguments(info, spec.Argv);
        BuildEnvironment(info, spec.Env);

        var stdout = CollectSpec(spec.Stdio.Stdout, info, standardOutput: true);
        var stderr = CollectSpec(spec.Stdio.Stderr, info, standardOutput: false);
        var stdin = spec.Stdio.Stdin as DataStdin;
        if (stdin is not null)
        {
            info.RedirectStandardInput = true;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"subprocess: failed to spawn \"{spec.Argv[0]}\": {error.Message}", error);
        }
        var handle = new LocalHandle(process, stdout, stderr, stdin?.Data, spec.CancellationToken);
        lock (_gate) _live.Add(handle);
        handle.StartPumping();
        _ = handle.Done.ContinueWith(_ =>
        {
            lock (_gate) _live.Remove(handle);
        }, TaskScheduler.Default);
        return handle;
    }

    /// <summary>Teardown: terminate and await every still-live tree.</summary>
    public override async ValueTask StopAsync()
    {
        LocalHandle[] live;
        lock (_gate) live = _live.ToArray();
        foreach (var handle in live) handle.Terminate();
        foreach (var handle in live)
        {
            try
            {
                await handle.Done;
            }
            catch
            {
                // Contained at teardown; the handle records the spawn failure itself.
            }
        }
        await base.StopAsync();
    }

    /// <summary>Carry argv[1..] into the child without shell interpretation.</summary>
    private static void AppendArguments(ProcessStartInfo info, IReadOnlyList<string> argv)
    {
        foreach (var argument in argv.Skip(1)) info.ArgumentList.Add(argument);
    }

    /// <summary>Scrub the ambient <c>DSH_*</c> facts, then merge the spec's explicit entries (null tombstones remove).</summary>
    private static void BuildEnvironment(ProcessStartInfo info, IReadOnlyDictionary<string, string?>? env)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith(DshEnv.Prefix, StringComparison.Ordinal))
            {
                // The start-info environment inherits the parent's; a managed fact must be
                // explicitly removed, not merely skipped.
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

    /// <summary>Apply one output disposition to the start info and build its collector, when the stream is collected.</summary>
    private static OutputCollector? CollectSpec(SubprocessOutputMode mode, ProcessStartInfo info, bool standardOutput)
    {
        if (mode is not CollectOutput collect) return null;
        if (standardOutput) info.RedirectStandardOutput = true;
        else info.RedirectStandardError = true;
        return new OutputCollector(collect.Collect.MaxBytes, collect.Collect.SpillMaxBytes);
    }

    /// <summary>One live handle: tree termination, outcome settlement, stdin write, and offset-based readers.</summary>
    private sealed class LocalHandle : ISubprocessHandle
    {
        private readonly Process _process;
        private readonly OutputCollector? _stdout;
        private readonly OutputCollector? _stderr;
        private readonly string? _stdinData;
        private readonly TaskCompletionSource<SubprocessOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _terminated;
        private int _reading;

        public LocalHandle(Process process, OutputCollector? stdout, OutputCollector? stderr, string? stdinData, CancellationToken? ct)
        {
            _process = process;
            _stdout = stdout;
            _stderr = stderr;
            _stdinData = stdinData;
            Pid = process.Id;
            ct?.Register(() => Terminate());
        }

        public int Pid { get; }

        public Task<SubprocessOutcome> Done => _done.Task;

        public SubprocessCollectedOutputs Collected => new(_stdout, _stderr);

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
        }

        /// <summary>Start the output pumps, stdin write, and exit settlement once.</summary>
        public void StartPumping()
        {
            if (Interlocked.Exchange(ref _reading, 1) != 0) return;
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                var readStdout = _stdout is null ? null : Task.Run(() => _stdout.Pump(_process.StandardOutput));
                var readStderr = _stderr is null ? null : Task.Run(() => _stderr.Pump(_process.StandardError));
                if (_stdinData is not null)
                {
                    try
                    {
                        await _process.StandardInput.WriteAsync(_stdinData);
                        _process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // The child closed its stdin early; the batch shape writes what it can.
                    }
                }
                await _process.WaitForExitAsync();
                if (readStdout is not null) await readStdout;
                if (readStderr is not null) await readStderr;
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
    }

    /// <summary>
    /// Bounded collector for one stream: keeps the byte-cap TAIL in memory, optionally spills the
    /// complete stream to a temp file up to its own cap (an over-cap stream discards its now
    /// incomplete spill). Exposes offset-based reads over whole-stream coordinates.
    /// </summary>
    private sealed class OutputCollector : ISubprocessOutputReader
    {
        private readonly object _gate = new();
        private readonly int _maxBytes;
        private readonly int? _spillMaxBytes;
        private readonly MemoryStream _tail = new();
        private string? _spillPath;
        private Stream? _spill;
        private bool _spillIntact = true;
        private long _totalBytes;
        private long _tailStart;

        public OutputCollector(int maxBytes, int? spillMaxBytes)
        {
            _maxBytes = maxBytes;
            _spillMaxBytes = spillMaxBytes;
        }

        public void Pump(StreamReader reader)
        {
            if (_spillMaxBytes is not null)
            {
                _spillPath = Path.Combine(Path.GetTempPath(), "dsh-subprocess-" + Guid.NewGuid().ToString("N") + ".spill");
                _spill = new FileStream(_spillPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            }
            var buffer = new char[4096];
            while (true)
            {
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                var bytes = Encoding.UTF8.GetBytes(buffer, 0, read);
                lock (_gate)
                {
                    _totalBytes += bytes.Length;
                    AppendTail(bytes);
                    if (_spill is not null && _spillIntact)
                    {
                        _spill.Write(bytes, 0, bytes.Length);
                        if (_totalBytes > _spillMaxBytes!.Value)
                        {
                            _spillIntact = false;
                            _spill.Dispose();
                            _spill = null;
                            File.Delete(_spillPath);
                            _spillPath = null;
                        }
                    }
                }
            }
            if (_spill is not null)
            {
                _spill.Flush();
                _spill.Dispose();
                _spill = null;
            }
            reader.Dispose();
        }

        public SubprocessOutputRead ReadFrom(long fromByte)
        {
            lock (_gate)
            {
                var bytes = _tail.ToArray();
                var text = Encoding.UTF8.GetString(bytes);
                var dropped = _tailStart;
                var lossy = fromByte < dropped;
                var next = _totalBytes;
                return new SubprocessOutputRead(lossy ? text : text[(int)Math.Max(0, fromByte - dropped)..], next, lossy, _spillIntact ? _spillPath : null);
            }
        }

        private void AppendTail(byte[] bytes)
        {
            _tail.Write(bytes, 0, bytes.Length);
            if (_tail.Length <= _maxBytes) return;
            var excess = _tail.Length - _maxBytes;
            var kept = _tail.ToArray();
            _tail.SetLength(0);
            _tail.Write(kept, (int)excess, _maxBytes);
            _tailStart += excess;
        }
    }
}
