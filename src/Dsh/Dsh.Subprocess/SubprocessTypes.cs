using System.Diagnostics;

namespace Harness.Subprocess;

/// <summary>Namespace prefix reserved for DeepSeek Harness-managed child environment facts.</summary>
public static class DshEnv
{
    /// <summary>The reserved prefix.</summary>
    public const string Prefix = "DSH_";
}

/// <summary>One captured stream: the (possibly truncated) text plus recovery info.</summary>
public sealed record CollectedOutput(string Text, bool Truncated, string? SpillPath = null);

/// <summary>Bounded in-memory collection for one output stream, with an optional full-stream spill file.</summary>
public sealed record SubprocessCollect(int MaxBytes, int? SpillMaxBytes = null);

/// <summary>stdin disposition: ignore, or write these bytes and close.</summary>
public abstract record SubprocessStdinMode;

/// <summary>Leave stdin closed/empty.</summary>
public sealed record IgnoreStdin : SubprocessStdinMode;

/// <summary>Write the bytes, then close.</summary>
public sealed record DataStdin(string Data) : SubprocessStdinMode;

/// <summary>stdout/stderr disposition: inherit the parent handle, or boundedly collect.</summary>
public abstract record SubprocessOutputMode;

/// <summary>Pass the parent's descriptor through.</summary>
public sealed record InheritOutput : SubprocessOutputMode;

/// <summary>Buffer boundedly, keeping the tail, with an optional full-stream spill file.</summary>
public sealed record CollectOutput(SubprocessCollect Collect) : SubprocessOutputMode;

/// <summary>Per-stream stdio dispositions, all explicit — this seam applies no defaults.</summary>
public sealed record SubprocessStdio(SubprocessStdinMode Stdin, SubprocessOutputMode Stdout, SubprocessOutputMode Stderr);

/// <summary>
/// A fully-specified spawn request. This seam applies no defaults: every disposition, limit, and
/// directory is explicit, so the caller's own config decides them (the request/spec split).
/// </summary>
public sealed record SubprocessSpawnSpec(
    IReadOnlyList<string> Argv,
    string Cwd,
    SubprocessStdio Stdio,
    int GraceMs,
    CancellationToken? CancellationToken = null,
    IReadOnlyDictionary<string, string?>? Env = null);

/// <summary>Exit facts of one closed process; no timeout/cancellation classification and no output here.</summary>
public sealed record SubprocessOutcome(int? ExitCode, string? Signal);

/// <summary>One incremental read of a collected stream.</summary>
public sealed record SubprocessOutputRead(string Text, long NextOffset, bool Lossy, string? SpillPath = null);

/// <summary>Cursor-free incremental access to one collected output stream (whole-stream byte offsets).</summary>
public interface ISubprocessOutputReader
{
    /// <summary>
    /// Read everything captured since <paramref name="fromByte"/>; when that offset slid out of the
    /// in-memory tail window the read is lossy and returns the whole retained tail.
    /// </summary>
    SubprocessOutputRead ReadFrom(long fromByte);
}

/// <summary>Offset-based readers for the streams spawned in collect mode.</summary>
public sealed record SubprocessCollectedOutputs(ISubprocessOutputReader? Stdout, ISubprocessOutputReader? Stderr);

/// <summary>
/// A live child process rooted in its own process tree. Collected output remains readable after
/// exit. Termination is tree-scoped: on Windows this kills the whole tree immediately (there is
/// no SIGTERM; the grace period is the POSIX escalation slot, a no-op here).
/// </summary>
public interface ISubprocessHandle
{
    /// <summary>Process id (tree root); -1 when the spawn itself failed.</summary>
    int Pid { get; }

    /// <summary>Resolves at process close with exit facts; rejects only for spawn-level failures.</summary>
    Task<SubprocessOutcome> Done { get; }

    /// <summary>Offset-based readers for collect-mode streams (also readable after exit).</summary>
    SubprocessCollectedOutputs Collected { get; }

    /// <summary>Terminate the process tree. Idempotent; also triggered by the spec's cancellation token.</summary>
    void Terminate();

    /// <summary>Wait until the tree exits, or until <paramref name="ct"/> fires first.</summary>
    Task<bool> WaitForExitAsync(CancellationToken? ct = null);
}

/// <summary>Service Definition for the subprocess capability: fully-specified spawn requests, bounded collected output with spill recovery, and tree-scoped termination.</summary>
public interface ISubprocessService
{
    /// <summary>Spawn one child process per <paramref name="spec"/>.</summary>
    ISubprocessHandle Spawn(SubprocessSpawnSpec spec);
}

/// <summary>Escalate termination on one process tree (Windows: immediate whole-tree kill).</summary>
public static class ProcessTree
{
    /// <summary>Kill the whole tree rooted at <paramref name="process"/>.</summary>
    public static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between check and kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The tree root is gone.
        }
    }
}
