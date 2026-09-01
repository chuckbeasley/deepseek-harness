namespace Harness.Guard;

/// <summary>
/// The loop-hygiene guard seam (port of packages/guard): guards observe tool-call streams and
/// tool execution on the owner context and return advisory input — reminders about repeated calls,
/// or timeout deadlines — without vetoing or rewriting calls. Each guard registers itself as a
/// context service under its own <c>guard:&lt;name&gt;</c> key and implements this surface so a
/// consumer can enumerate the active guards and ask what deadline they arm.
/// </summary>
public interface IGuardService
{
    /// <summary>The stable guard name (e.g. "repeat-tool-reminder").</summary>
    string Name { get; }

    /// <summary>
    /// The effective timeout budget this guard arms for one tool call in milliseconds, or null
    /// when the guard arms no deadline for the tool. A guard without a timeout policy returns
    /// null for every tool.
    /// </summary>
    /// <param name="toolName">the tool name a call targets.</param>
    long? TimeoutMsFor(string toolName);
}
