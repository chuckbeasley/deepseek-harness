using Harness.Sandbox;
using Harness.Subprocess;

namespace Harness.Shell;

/// <summary>
/// Model-facing result rendering for the shell tool (port of the tool-bash render module): stdout,
/// then a marked stderr section, then the sandbox denial marker (when the run was denied) and
/// interruption/exit markers. Non-zero exits are reported, not errored — the model decides how to
/// react.
/// </summary>
public static class ShellRender
{
    /// <summary>Shape one finished run into the text the model sees.</summary>
    public static string RenderResult(ShellRunResult result)
    {
        var body = StreamText(result.Stdout);
        var err = StreamText(result.Stderr);
        if (err.Length > 0)
        {
            if (body.Length > 0 && !body.EndsWith('\n')) body += "\n";
            body += "[stderr]\n" + err;
        }
        if (body.Length == 0) body = "(no output)";

        var markers = new List<string>();
        if (result.Sandbox is { Denied: true } sandbox)
        {
            markers.Add(SandboxEscalation.SandboxDenialMarker(sandbox.Mode));
        }
        if (result.TimedOut) markers.Add($"[timed out after {result.TimeoutMs}ms]");
        if (result.Signal is not null) markers.Add($"[killed by signal: {result.Signal}]");
        else if (result.ExitCode is not null && result.ExitCode != 0) markers.Add($"[exit code: {result.ExitCode}]");
        if (markers.Count == 0) return body;
        if (!body.EndsWith('\n')) body += "\n";
        return body + string.Join('\n', markers);
    }

    /// <summary>Append the truncation notice (with the full-output spill path) to a stream's text.</summary>
    private static string StreamText(CollectedOutput output)
        => output.Truncated
            ? $"{output.Text}\n[output truncated; full output: {output.SpillPath ?? "(unavailable)"}]"
            : output.Text;
}
