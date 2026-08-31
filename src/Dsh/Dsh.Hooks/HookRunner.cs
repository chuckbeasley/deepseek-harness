using System.Diagnostics;
using System.Text.Json;
using Dsh.Shell;

namespace Dsh.Hooks;

/// <summary>
/// Execute command hooks through the shell service (port of the TS <c>runHook</c>), using its
/// timeout machinery and the caller's cancellation. The bridge supplies the trusted stdin payload
/// and dialect environment, then this module decodes the captured outcome. Infrastructure rejection
/// becomes an outcome with no exit code, so a hook run never throws into the calling turn.
/// </summary>
public static class HookRunner
{
    /// <summary>The reference default per-hook timeout, in ms (10 minutes) — the value both Claude Code and Codex apply to a hook whose config sets no <c>timeout</c>.</summary>
    public const int DefaultHookTimeoutMs = 600_000;

    /// <summary>Everything a single hook invocation needs beyond its command line.</summary>
    public sealed record RunHookOptions(
        /// <summary>The JSON payload object written to the hook's stdin (the bridge builds it).</summary>
        object Payload,
        /// <summary>Extra env vars for the hook process (<c>CLAUDE_PROJECT_DIR</c>, …; the bridge builds these).</summary>
        IReadOnlyDictionary<string, string>? Env,
        /// <summary>Working directory for the hook (omitted uses the executor's default).</summary>
        string? Cwd,
        /// <summary>Explicit owning-operation signal; firing it cancels the hook run.</summary>
        CancellationToken CancellationToken,
        /// <summary>Whether to append a trailing newline to the stdin payload (CC yes, Codex no).</summary>
        bool TrailingNewline,
        /// <summary>Timeout applied when the hook's config sets no <c>timeout</c> of its own (the bridge owns the default).</summary>
        int DefaultTimeoutMs,
        /// <summary>
        /// The event this hook is firing for (e.g. <c>PreToolUse</c>). When set, a structured
        /// <c>hookSpecificOutput</c> block whose <c>hookEventName</c> names a DIFFERENT event is
        /// treated as malformed and its event-scoped fields are discarded. Omit it to apply any
        /// block as-is.
        /// </summary>
        string? ExpectedEventName);

    /// <summary>The <see cref="HookOutput"/> plus the wall-clock duration of the run (for <c>hook/result</c>).</summary>
    public sealed record RunHookResult(HookOutput Output, long DurationMs);

    /// <summary>
    /// Run <paramref name="hook"/> with serialized stdin and decode its outcome. A hook-specific
    /// timeout in seconds overrides the default; trusted environment entries merge after the
    /// executor scrub.
    /// </summary>
    /// <param name="shell">the executor service the command runs through.</param>
    /// <param name="hook">the configured command; its <c>TimeoutSec</c> (wire unit: seconds) overrides the default timeout.</param>
    /// <param name="options">the invocation's payload, env, cwd, signal, stdin framing, and default timeout.</param>
    /// <returns>the decoded output plus the run's wall-clock duration.</returns>
    public static RunHookResult RunHook(IShellService shell, CommandHook hook, RunHookOptions options)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(hook);
        var started = Stopwatch.GetTimestamp();
        var timeoutMs = hook.TimeoutSec is int seconds ? seconds * 1000 : options.DefaultTimeoutMs;
        var stdin = JsonSerializer.Serialize(options.Payload) + (options.TrailingNewline ? "\n" : "");
        try
        {
            var result = shell.Run(shell.Resolve(new ShellExecRequest(
                hook.Command,
                Workdir: options.Cwd,
                TimeoutMs: timeoutMs,
                Stdin: stdin,
                CancellationToken: options.CancellationToken,
                Env: options.Env)));
            // ShellRunResult.ExitCode is null when the process died by signal; the protocol's
            // exit-code contract is numeric, so a signal death maps to undefined (a non-blocking
            // error — no clean exit code to act on).
            return new RunHookResult(
                HookCodec.ParseHookOutput(result.ExitCode, result.Stdout.Text, result.Stderr.Text, options.ExpectedEventName),
                Stopwatch.GetElapsedTime(started).Milliseconds);
        }
        catch (Exception error)
        {
            // The executor rejects only on infrastructure faults (an unusable workdir). A hook
            // that cannot run is a non-blocking error: no exit code, the failure on stderr for
            // the record. The turn proceeds.
            return new RunHookResult(
                HookCodec.ParseHookOutput(null, "", error.Message),
                Stopwatch.GetElapsedTime(started).Milliseconds);
        }
    }
}
