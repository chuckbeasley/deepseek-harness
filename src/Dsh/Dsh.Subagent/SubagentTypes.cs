namespace Dsh.Subagent;

/// <summary>Registry-minted subagent identity.</summary>
public readonly record struct SubagentId(string Value)
{
    public static implicit operator string(SubagentId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Lifecycle state of one delegated subagent.</summary>
public enum SubagentStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A caller's delegation request: the task text and an optional display label.</summary>
public sealed record SubagentRequest(string Task, string? Label = null);

/// <summary>The settled result of one delegation.</summary>
public sealed record SubagentResult(string Text, bool IsError);

/// <summary>
/// A live delegation handle: the only access path to a running subagent's lifecycle, result, and
/// cancellation. Settles exactly once.
/// </summary>
public interface ISubagentHandle
{
    /// <summary>The delegation identity.</summary>
    SubagentId Id { get; }

    /// <summary>The current lifecycle state.</summary>
    SubagentStatus Status { get; }

    /// <summary>Resolves at settlement with the result (never rejects; a failed body settles Failed with the error text).</summary>
    Task<SubagentResult> Done { get; }

    /// <summary>Cancel the delegation (idempotent; false once settled).</summary>
    bool Cancel();
}

/// <summary>Service Definition for the subagent capability: delegate a task to an in-process driver.</summary>
public interface ISubagentService
{
    /// <summary>Delegate one task and return its handle immediately.</summary>
    ISubagentHandle Delegate(SubagentRequest request);
}
