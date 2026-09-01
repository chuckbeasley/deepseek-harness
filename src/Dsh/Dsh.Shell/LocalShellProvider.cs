using Harness.Cordis.Core;
using Harness.Subprocess;

namespace Harness.Shell;

/// <summary>
/// Local shell executor (ctx.shell; port of the bash/pwsh-local family folded to one configured
/// shell). Each run composes the shell invocation over the subprocess seam with a fused
/// timeout/abort deadline: the FIRST of the executor timeout and the caller's cancellation to
/// fire owns the cause classification, so a command that traps the kill and exits 0 still reports
/// the interruption. The ambient environment is scrubbed by the subprocess provider.
/// </summary>
public sealed class LocalShellProvider : Service, IShellService
{
    private readonly ShellConfig _config;

    /// <summary>Create the executor and register it as <c>shell</c>.</summary>
    public LocalShellProvider(Context ctx, ShellConfig? config = null)
        : base(ctx, "shell")
    {
        _config = config ?? new ShellConfig();
        if (string.IsNullOrWhiteSpace(_config.ShellPath))
        {
            throw new ArgumentException("shell: ShellPath must be non-empty", nameof(config));
        }
        if (_config.TimeoutMs <= 0 || _config.StdoutMaxBytes <= 0 || _config.StderrMaxBytes <= 0)
        {
            throw new ArgumentException("shell: TimeoutMs, StdoutMaxBytes, and StderrMaxBytes must be positive", nameof(config));
        }
    }

    /// <inheritdoc />
    public ShellExecSpec Resolve(ShellExecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command.Trim().Length == 0)
        {
            throw new ArgumentException("shell: command must be a non-empty string", nameof(request));
        }
        if (request.TimeoutMs is int timeout && timeout <= 0)
        {
            throw new ArgumentException("shell: timeoutMs must be positive", nameof(request));
        }
        var workdir = request.Workdir is null || request.Workdir.Length == 0
            ? _config.DefaultWorkdir
            : Path.GetFullPath(Path.IsPathRooted(request.Workdir)
                ? request.Workdir
                : Path.Combine(_config.DefaultWorkdir, request.Workdir));
        return new ShellExecSpec(
            request.Command,
            workdir,
            request.TimeoutMs ?? _config.TimeoutMs,
            _config.StdoutMaxBytes,
            request.Stdin,
            request.CancellationToken,
            request.Env);
    }

    /// <inheritdoc />
    public ShellRunResult Run(ShellExecSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var subprocess = Ctx.Get<ISubprocessService>("subprocess")
            ?? throw new InvalidOperationException("shell: the \"subprocess\" service is required");
        if (!Directory.Exists(spec.Workdir))
        {
            throw new InvalidOperationException($"shell: workdir \"{spec.Workdir}\" does not exist");
        }
        var timeout = new CancellationTokenSource(spec.TimeoutMs);
        var firstCause = new FirstCause();
        var timedOut = timeout.Token.Register(firstCause.Timeout);
        var aborted = spec.CancellationToken?.Register(firstCause.Abort);

        try
        {
            var argv = ShellArgv(spec);
            var spawn = new SubprocessSpawnSpec(
                argv,
                spec.Workdir,
                new SubprocessStdio(
                    spec.Stdin is null ? new IgnoreStdin() : new DataStdin(spec.Stdin),
                    new CollectOutput(new SubprocessCollect(spec.StdoutMaxBytes, spec.StdoutMaxBytes * 4)),
                    new CollectOutput(new SubprocessCollect(_config.StderrMaxBytes, _config.StderrMaxBytes * 4))),
                GraceMs: 5000,
                CancellationToken: firstCause.Token,
                Env: spec.Env);
            var handle = subprocess.Spawn(spawn);
            var outcome = handle.Done.GetAwaiter().GetResult();
            var stdout = ReadAll(handle.Collected.Stdout);
            var stderr = ReadAll(handle.Collected.Stderr);
            return new ShellRunResult(
                outcome.ExitCode,
                outcome.Signal,
                firstCause.Result == Cause.Timeout,
                firstCause.Result == Cause.Aborted,
                spec.TimeoutMs,
                stdout,
                stderr,
                null);
        }
        catch (OperationCanceledException) when (firstCause.Result != Cause.None)
        {
            // The fused deadline fired mid-spawn; classify and report the kill as an empty run.
            return new ShellRunResult(
                null,
                null,
                firstCause.Result == Cause.Timeout,
                firstCause.Result == Cause.Aborted,
                spec.TimeoutMs,
                new CollectedOutput(string.Empty, false),
                new CollectedOutput(string.Empty, false),
                null);
        }
        finally
        {
            timedOut.Dispose();
            aborted?.Dispose();
            timeout.Dispose();
        }
    }

    /// <summary>The shell invocation argv for the configured shell (never shell-interpreted twice).</summary>
    private string[] ShellArgv(ShellExecSpec spec) => _config.ShellPath switch
    {
        "cmd.exe" or "cmd" => new[] { "cmd.exe", "/d", "/s", "/c", spec.Command },
        "pwsh.exe" or "pwsh" => new[] { "pwsh.exe", "-NoLogo", "-NoProfile", "-Command", spec.Command },
        _ => new[] { _config.ShellPath, "-c", spec.Command },
    };

    private static Subprocess.CollectedOutput ReadAll(ISubprocessOutputReader? reader)
        => reader is null ? new Subprocess.CollectedOutput(string.Empty, false) : FromRead(reader.ReadFrom(0));

    private static Subprocess.CollectedOutput FromRead(SubprocessOutputRead read)
        => new(read.Text, read.Lossy, read.SpillPath);

    /// <summary>Fused deadline bookkeeping: the first cause to fire wins and drives the child kill.</summary>
    private sealed class FirstCause
    {
        private readonly CancellationTokenSource _cts = new();
        private int _state;

        public CancellationToken Token => _cts.Token;

        public Cause Result => (Cause)Volatile.Read(ref _state);

        public void Timeout()
        {
            if (Interlocked.CompareExchange(ref _state, (int)Cause.Timeout, (int)Cause.None) == (int)Cause.None) _cts.Cancel();
        }

        public void Abort()
        {
            if (Interlocked.CompareExchange(ref _state, (int)Cause.Aborted, (int)Cause.None) == (int)Cause.None) _cts.Cancel();
        }
    }

    private enum Cause
    {
        None,
        Timeout,
        Aborted,
    }
}
