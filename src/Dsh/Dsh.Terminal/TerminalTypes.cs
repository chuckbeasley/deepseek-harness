namespace Dsh.Terminal;

/// <summary>Registry-minted terminal session identity.</summary>
public readonly record struct TerminalSessionId(string Value)
{
    public static implicit operator string(TerminalSessionId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Top-level process status of one terminal session.</summary>
public abstract record TerminalSessionStatus
{
    /// <summary>The process is running.</summary>
    public sealed record Running : TerminalSessionStatus;

    /// <summary>The process exited with these facts.</summary>
    public sealed record Exited(int? ExitCode, string? Signal) : TerminalSessionStatus;
}

/// <summary>Why one interactive send returned control to its caller.</summary>
public enum TerminalWaitReason
{
    SessionExit,
    Timeout,
    StdinRead,
    InferredIdle,
}

/// <summary>Request to create one terminal session.</summary>
public sealed record TerminalOpenRequest(string Type, string? Name = null, string? Cwd = null);

/// <summary>Input for one line-oriented terminal interaction.</summary>
public sealed record TerminalSendRequest(string Text, bool Submit);

/// <summary>Incremental output consumed from one live send operation.</summary>
public sealed record TerminalSendRead(string Delta, bool Truncated);

/// <summary>Settled result of one send operation.</summary>
public sealed record TerminalSendResult(
    string Viewport,
    TerminalWaitReason WaitReason,
    TerminalSessionStatus SessionStatus,
    bool Truncated);

/// <summary>A live send operation; exactly one may be active per session.</summary>
public interface ITerminalSendOperation
{
    /// <summary>Resolves after readiness, timeout, cancellation, or top-level process exit.</summary>
    Task<TerminalSendResult> Done { get; }

    /// <summary>Consume output produced since the prior call.</summary>
    TerminalSendRead ReadOutput();

    /// <summary>Interrupt the send (idempotent; false after settlement).</summary>
    bool Cancel();
}

/// <summary>One backward scrollback page request.</summary>
public sealed record TerminalReadRequest(int? Offset = null, int? Count = null);

/// <summary>A bounded scrollback page.</summary>
public sealed record TerminalReadResult(string Text, int TotalLines, int LineBegin, int LineEnd, bool Truncated);

/// <summary>Owner-visible summary of one terminal session.</summary>
public sealed record TerminalSessionSnapshot(
    TerminalSessionId SessionId,
    string? Name,
    string Type,
    int? Pid,
    TerminalSessionStatus Status);

/// <summary>A live terminal session retained by the service.</summary>
public interface ITerminalSession
{
    /// <summary>The registry-minted session identity.</summary>
    TerminalSessionId SessionId { get; }

    /// <summary>Owner-local display name from the open request, when one was given.</summary>
    string? Name { get; }

    /// <summary>Initial bounded terminal output returned from open.</summary>
    string Motd { get; }

    /// <summary>Top-level process id, when one exists.</summary>
    int? Pid { get; }

    /// <summary>Start one exclusive send operation.</summary>
    ITerminalSendOperation StartSend(TerminalSendRequest request);

    /// <summary>Read one bounded page from retained scrollback.</summary>
    TerminalReadResult Read(TerminalReadRequest request);

    /// <summary>Observe the top-level process status.</summary>
    TerminalSessionStatus Status();

    /// <summary>Idempotently close the owned process tree and await quiescence.</summary>
    Task CloseAsync(string reason);
}

/// <summary>Service Definition for the terminal capability: owner-visible persistent terminal sessions.</summary>
public interface ITerminalService
{
    /// <summary>Open one terminal session of the given backend type.</summary>
    Task<ITerminalSession> OpenAsync(TerminalOpenRequest request);

    /// <summary>Snapshots of every live session, in creation order.</summary>
    IReadOnlyList<TerminalSessionSnapshot> List();
}
