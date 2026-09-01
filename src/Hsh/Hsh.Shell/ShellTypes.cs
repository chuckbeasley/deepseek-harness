using Harness.Sandbox;

namespace Harness.Shell;

/// <summary>Deployment-varying shell executor config; no tunable is hardcoded.</summary>
public sealed record ShellConfig
{
    /// <summary>The shell executable that runs each command (default: cmd.exe on Windows, sh elsewhere).</summary>
    public string ShellPath { get; init; } = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

    /// <summary>Default working directory when a request names none (default: the process cwd).</summary>
    public string DefaultWorkdir { get; init; } = Environment.CurrentDirectory;

    /// <summary>Default foreground timeout in milliseconds (default: 120000).</summary>
    public int TimeoutMs { get; init; } = 120_000;

    /// <summary>Default foreground stdout capture budget in bytes (default: 256 KiB).</summary>
    public int StdoutMaxBytes { get; init; } = 256 * 1024;

    /// <summary>Foreground stderr capture budget in bytes (default: 64 KiB).</summary>
    public int StderrMaxBytes { get; init; } = 64 * 1024;
}

/// <summary>
/// A caller's execution REQUEST: <see cref="Workdir"/> and <see cref="TimeoutMs"/> are optional and
/// filled by <see cref="IShellService.Resolve"/> from the implementation's config.
/// </summary>
public sealed record ShellExecRequest(
    string Command,
    string? Workdir = null,
    int? TimeoutMs = null,
    string? Stdin = null,
    CancellationToken? CancellationToken = null,
    /// <summary>Extra child environment entries merged after the ambient scrub (the hook bridges' dialect env).</summary>
    IReadOnlyDictionary<string, string>? Env = null);

/// <summary>A resolved execution spec (defaults filled and capped by <see cref="IShellService.Resolve"/>).</summary>
public sealed record ShellExecSpec(
    string Command,
    string Workdir,
    int TimeoutMs,
    int StdoutMaxBytes,
    string? Stdin,
    CancellationToken? CancellationToken,
    /// <summary>Extra child environment entries merged after the ambient scrub.</summary>
    IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// The outcome of one completed (or killed) foreground run. <see cref="Sandbox"/> carries the
/// sandbox execution facts when a sandboxing executor handled the run; the unsandboxed local
/// provider always leaves it null.
/// </summary>
public sealed record ShellRunResult(
    int? ExitCode,
    string? Signal,
    bool TimedOut,
    bool Aborted,
    int TimeoutMs,
    Subprocess.CollectedOutput Stdout,
    Subprocess.CollectedOutput Stderr,
    ShellSandboxInfo? Sandbox = null);

/// <summary>Service Definition for the shell capability: foreground command runs over the subprocess seam.</summary>
public interface IShellService
{
    /// <summary>Apply implementation-owned defaults and caps to a request before execution.</summary>
    ShellExecSpec Resolve(ShellExecRequest request);

    /// <summary>
    /// Run a command in the foreground; resolves when it finishes. Nonzero exits, timeout kills,
    /// and abort kills resolve with a descriptive result; only infrastructure failures reject.
    /// </summary>
    ShellRunResult Run(ShellExecSpec spec);
}
