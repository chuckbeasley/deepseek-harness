using Harness.Session;

namespace Harness.Hooks;

/// <summary>
/// Append helpers for durable, log-only hook events (port of the TS <c>appendHookInvoked</c> /
/// <c>appendHookResult</c>). They carry no surface intent and must remain turn-enclosed and
/// invoked/result paired. Mid-turn hook points satisfy that boundary; SessionStart records
/// injected context instead and does not append <c>hook/*</c> outside a turn.
/// </summary>
public static class HookLog
{
    /// <summary>The reference default for the <c>hook/result</c> stderr summary cap (both bridges' config default).</summary>
    public const int DefaultStderrSummaryMaxChars = 500;

    /// <summary>
    /// Truncate a hook's stderr for the persisted summary: trimmed, <c>null</c> when empty, cut at
    /// <paramref name="maxChars"/> with an ellipsis when over.
    /// </summary>
    /// <param name="stderr">the hook's raw captured stderr.</param>
    /// <param name="maxChars">the character cap for the summary (the bridge's config value).</param>
    /// <returns>the trimmed, capped summary, or <c>null</c> when stderr is blank.</returns>
    public static string? SummarizeStderr(string stderr, int maxChars)
    {
        var trimmed = stderr.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > maxChars ? trimmed[..maxChars] + "…" : trimmed;
    }

    /// <summary>Append a <c>hook/invoked</c> event naming the handler and hook point to <paramref name="session"/>.</summary>
    /// <param name="session">the session whose open turn records the event.</param>
    /// <param name="turn">the open turn the invocation lives inside.</param>
    /// <param name="point">the hook point (<c>PreToolUse</c>, <c>Stop</c>, …).</param>
    /// <param name="dialect">the bridge dialect that ran it.</param>
    /// <param name="handlerId">a stable id correlating the invoked event with its result.</param>
    /// <param name="matcher">the matcher-group pattern that selected it (absent for match-all).</param>
    public static void AppendInvoked(Harness.Session.Session session, long turn, string point, HookDialect dialect, string handlerId, string? matcher = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Append(new HookInvokedEvent
        {
            Turn = turn,
            Point = point,
            Dialect = dialect,
            HandlerId = handlerId,
            Matcher = matcher,
        });
    }

    /// <summary>Append the durable result paired with <c>hook/invoked</c>.</summary>
    /// <param name="session">the session whose open turn records the event.</param>
    /// <param name="turn">the open turn the invocation lives inside.</param>
    /// <param name="point">the hook point.</param>
    /// <param name="handlerId">the correlating invocation id.</param>
    /// <param name="output">the decoded outcome the run produced.</param>
    /// <param name="stderrSummaryMaxChars">character cap for the derived stderr summary.</param>
    /// <param name="durationMs">wall-clock duration of the run — durable audit timing.</param>
    public static void AppendResult(Harness.Session.Session session, long turn, string point, string handlerId, HookOutput output, int stderrSummaryMaxChars, long durationMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        var decision = output.Decision ?? (output.Continue == false ? "stop" : "pass");
        session.Append(new HookResultEvent
        {
            Turn = turn,
            Point = point,
            HandlerId = handlerId,
            Decision = decision,
            ExitCode = output.ExitCode,
            StderrSummary = SummarizeStderr(output.Stderr, stderrSummaryMaxChars),
            DurationMs = durationMs,
        });
    }
}
