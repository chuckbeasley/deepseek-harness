using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsh.Snapshot.Tests;

/// <summary>One parsed session-scenario manifest (snapshot.yml).</summary>
public sealed record ScenarioManifest(
    string Name,
    string Composition,
    string? Platform,
    string? Permission,
    string? HeaderClass,
    string? WorkspaceSetup,
    string? WorkspaceParent,
    bool WorkspaceFinal,
    bool ReplayOverride,
    string? InputTask,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// The headless session corpus runner (port of the TS headless.snapshot.ts): every
/// <c>snapshots/session/*</c> scenario replays through the real dsh CLI and its persisted log,
/// stdout, and stderr are compared with the recorded fixture. The runner reports PASS / DRIFT /
/// SKIP per scenario; the drift reasons name the first divergence.
/// </summary>
public static class CorpusTests
{
    private static readonly string[] PackedRowTypes = { "text-chunks", "reasoning-chunks", "tool-call-chunks" };

    public static int RunCorpus(Action<string> report)
    {
        var scenariosDir = Path.Combine(SnapshotDriver.RepoRoot(), "snapshots", "session");
        var scenarios = new List<ScenarioManifest>();
        foreach (var dir in Directory.EnumerateDirectories(scenariosDir).OrderBy(path => path, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, "snapshot.yml");
            if (!File.Exists(manifestPath)) continue;
            var manifest = ParseManifest(Path.GetFileName(dir), File.ReadAllText(manifestPath));
            if (manifest is not null) scenarios.Add(manifest);
        }
        var compositionOwners = CompositionOwners(scenariosDir);

        var passed = 0;
        var drifted = 0;
        var skipped = 0;
        var errored = 0;
        foreach (var scenario in scenarios)
        {
            if (scenario.Platform == "posix" && OperatingSystem.IsWindows())
            {
                report($"{scenario.Name}: SKIP (posix-only)");
                skipped++;
                continue;
            }
            if (scenario.Platform == "pwsh")
            {
                report($"{scenario.Name}: SKIP (needs PowerShell 7)");
                skipped++;
                continue;
            }
            try
            {
                var outcome = RunScenario(scenario, compositionOwners);
                if (outcome.Reasons.Count == 0)
                {
                    report($"{scenario.Name}: PASS");
                    passed++;
                }
                else
                {
                    report($"{scenario.Name}: DRIFT — {string.Join("; ", outcome.Reasons)}");
                    drifted++;
                }
            }
            catch (Exception error)
            {
                report($"{scenario.Name}: ERROR — {error.Message}");
                errored++;
            }
        }
        report($"corpus: {passed} passed, {drifted} drifted, {skipped} skipped, {errored} errored of {scenarios.Count}");
        return drifted + errored == 0 ? 0 : 1;
    }

    private sealed record ScenarioOutcome(List<string> Reasons);

    private static ScenarioOutcome RunScenario(ScenarioManifest scenario, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var reasons = new List<string>();
        var scenariosDir = Path.Combine(SnapshotDriver.RepoRoot(), "snapshots", "session");
        var dir = Path.Combine(scenariosDir, scenario.Name);
        var fixtureFile = Path.Combine(dir, "session.jsonl");
        var fixture = File.ReadAllText(fixtureFile);
        var task = TaskFromSession(fixture) ?? scenario.InputTask;
        if (task is null) throw new InvalidOperationException("no accepted or exceptional task input");
        var model = ModelFromSession(fixture);
        var expectedExit = (TurnReasonKind(fixture) == "completed" || (TurnReasonKind(fixture) is null && scenario.InputTask is not null)) ? 0 : 1;

        var (home, cwd) = SnapshotDriver.CreateRunDirs();
        try
        {
            SnapshotDriver.SeedWorkspace(cwd, Path.Combine(dir, "workspace"));
            PrepareWorkspace(cwd, scenario.WorkspaceSetup);
            var env = new Dictionary<string, string>(scenario.Environment);
            if (scenario.Permission is not null) env["DSH_PERMISSION_MODE"] = scenario.Permission;
            ApplyScenarioEnv(env, scenario, model.Model, scenariosDir, compositionOwners);
            var childFiles = ChildFilesEnv(dir);
            if (childFiles is not null) env["DSH_SNAPSHOT_CHILD_FILES"] = childFiles;
            var result = SnapshotDriver.RunHeadless(home, cwd, task, fixtureFile,
                provider: model.Provider, model: model.Model, extraEnv: env);
            var actualLog = SnapshotDriver.HarvestSessionLog(home);

            var expectedStdout = FinalTextFromSession(fixture) + "\n";
            if (result.Stdout != expectedStdout)
            {
                reasons.Add($"stdout: expected {JsonSerializer.Serialize(expectedStdout)} got {JsonSerializer.Serialize(result.Stdout)}");
            }
            var expectedStderr = StderrFromSession(fixture);
            if (result.Stderr != expectedStderr)
            {
                reasons.Add($"stderr: expected {JsonSerializer.Serialize(expectedStderr)} got {JsonSerializer.Serialize(result.Stderr)}");
            }
            if (result.ExitCode != expectedExit)
            {
                reasons.Add($"exit: expected {expectedExit} got {result.ExitCode}");
            }
            if (actualLog is null)
            {
                reasons.Add("no persisted session log");
                return new ScenarioOutcome(reasons);
            }
            var fixtureLogs = FixtureLogs(dir);
            var fixtureCount = fixtureLogs.Length;
            var actualCount = SessionLogCount(home);
            if (actualCount != fixtureCount)
            {
                reasons.Add($"session count: expected {fixtureCount} got {actualCount}");
            }
            var ctx = new NormalizeContext(CwdOf(actualLog) ?? "", Array.Empty<string>());
            var fixtureCtx = new NormalizeContext(CwdOf(fixture) ?? "", Array.Empty<string>());
            string[] actualNormalized;
            string[] fixtureNormalized;
            string redactedLog;
            string normalizedLog;
            string scrubbedLog;
            try { redactedLog = SnapshotNormalizer.RedactSessionSnapshotIds(new[] { actualLog })[0]; }
            catch (Exception normalizeError) { throw new InvalidOperationException($"redact: {normalizeError.Message}", normalizeError); }
            try { normalizedLog = SnapshotNormalizer.NormalizeSessionLog(redactedLog, ctx); }
            catch (Exception normalizeError) { throw new InvalidOperationException($"normalize: {normalizeError.Message}", normalizeError); }
            try { scrubbedLog = SnapshotNormalizer.ScrubSessionSnapshot(normalizedLog); }
            catch (Exception normalizeError) { throw new InvalidOperationException($"scrub: {normalizeError.Message}", normalizeError); }
            try { actualNormalized = new[] { SnapshotNormalizer.RepackSessionSnapshot(scrubbedLog) }; }
            catch (Exception normalizeError) { throw new InvalidOperationException($"repack: {normalizeError.Message}", normalizeError); }
            try
            {
                fixtureNormalized = SnapshotNormalizer.NormalizeSessionSnapshots(new[] { fixture }, fixtureCtx);
            }
            catch (Exception normalizeError)
            {
                throw new InvalidOperationException($"normalize fixture: {normalizeError.Message}", normalizeError);
            }
            if (actualNormalized[0] != fixtureNormalized[0])
            {
                reasons.Add(FirstLogDifference(fixtureNormalized[0], actualNormalized[0]));
            }
            var unknownTool = UnknownToolError(actualLog);
            if (unknownTool is not null)
            {
                reasons.Add($"unknown tool {JsonSerializer.Serialize(unknownTool)}");
            }
            return new ScenarioOutcome(reasons);
        }
        finally
        {
            var root = Path.GetDirectoryName(home);
            if (root is not null)
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Run one scenario and print the full expected/actual comparison (the diff mode).</summary>
    public static void DiffScenario(string name, Action<string> report)
    {
        var scenariosDir = Path.Combine(SnapshotDriver.RepoRoot(), "snapshots", "session");
        var dir = Path.Combine(scenariosDir, name);
        if (!Directory.Exists(dir))
        {
            report($"no such scenario: {name}");
            return;
        }
        var manifest = ParseManifest(name, File.ReadAllText(Path.Combine(dir, "snapshot.yml")));
        if (manifest is null)
        {
            report($"{name}: not a headless scenario");
            return;
        }
        var fixtureFile = Path.Combine(dir, "session.jsonl");
        var fixture = File.ReadAllText(fixtureFile);
        var task = TaskFromSession(fixture) ?? manifest.InputTask;
        if (task is null)
        {
            report($"{name}: no task");
            return;
        }
        var model = ModelFromSession(fixture);
        var expectedExit = (TurnReasonKind(fixture) == "completed" || (TurnReasonKind(fixture) is null && manifest.InputTask is not null)) ? 0 : 1;
        var compositionOwners = CompositionOwners(scenariosDir);
        var (home, cwd) = SnapshotDriver.CreateRunDirs();
        try
        {
            SnapshotDriver.SeedWorkspace(cwd, Path.Combine(dir, "workspace"));
            PrepareWorkspace(cwd, manifest.WorkspaceSetup);
            var env = new Dictionary<string, string>(manifest.Environment);
            if (manifest.Permission is not null) env["DSH_PERMISSION_MODE"] = manifest.Permission;
            ApplyScenarioEnv(env, manifest, model.Model, scenariosDir, compositionOwners);
            var childFiles = ChildFilesEnv(dir);
            if (childFiles is not null) env["DSH_SNAPSHOT_CHILD_FILES"] = childFiles;
            var result = SnapshotDriver.RunHeadless(home, cwd, task, fixtureFile,
                provider: model.Provider, model: model.Model, extraEnv: env);
            var actualLog = SnapshotDriver.HarvestSessionLog(home) ?? "";
            var expectedStdout = FinalTextFromSession(fixture) + "\n";
            report($"stdout: expected {JsonSerializer.Serialize(expectedStdout)} got {JsonSerializer.Serialize(result.Stdout)} equal={result.Stdout == expectedStdout}");
            report($"stderr: expected {JsonSerializer.Serialize(StderrFromSession(fixture))} got {JsonSerializer.Serialize(result.Stderr)} equal={result.Stderr == StderrFromSession(fixture)}");
            report($"exit: expected {expectedExit} got {result.ExitCode}");
            var ctx = new NormalizeContext(CwdOf(actualLog) ?? "", Array.Empty<string>());
            var fixtureCtx = new NormalizeContext(CwdOf(fixture) ?? "", Array.Empty<string>());
            var actualNormalized = SnapshotNormalizer.NormalizeSessionSnapshots(new[] { actualLog }, ctx);
            var fixtureNormalized = SnapshotNormalizer.NormalizeSessionSnapshots(new[] { fixture }, fixtureCtx);
            var expectedLines = fixtureNormalized[0].Split('\n').Where(line => line.Length > 0).ToArray();
            var actualLines = actualNormalized[0].Split('\n').Where(line => line.Length > 0).ToArray();
            report($"log lines: expected {expectedLines.Length} got {actualLines.Length}");
            var count = Math.Min(expectedLines.Length, actualLines.Length);
            for (var index = 0; index < count; index++)
            {
                if (expectedLines[index] == actualLines[index]) continue;
                report($"--- line {index} ---");
                report($"EXP {expectedLines[index]}");
                report($"GOT {actualLines[index]}");
            }
            if (expectedLines.Length != actualLines.Length)
            {
                report($"--- length mismatch at line {count} ---");
                if (expectedLines.Length > count) report($"EXP {expectedLines[count]}");
                if (actualLines.Length > count) report($"GOT {actualLines[count]}");
            }
        }
        finally
        {
            var root = Path.GetDirectoryName(home);
            if (root is not null)
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    private static string[] FixtureLogs(string dir)
        => Directory.EnumerateFiles(dir, "session*.jsonl")
            .Where(path => Path.GetFileName(path) is "session.jsonl" or "session.1.jsonl" or "session.2.jsonl" or "session.3.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToArray();

    /// <summary>The persisted session-log count under the run home (parent plus any child sessions).</summary>
    private static int SessionLogCount(string home)
    {
        var root = Path.Combine(home, "profiles", "headless", "sessions");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "session.jsonl", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static string? CwdOf(string log)
    {
        var first = log.Split('\n').First(line => line.Trim().Length > 0);
        using var document = JsonDocument.Parse(first);
        return document.RootElement.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null;
    }

    private static string? UnknownToolError(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"tool/call\"", StringComparison.Ordinal)) continue;
            var call = JsonDocument.Parse(line).RootElement.GetProperty("data");
            var name = call.GetProperty("name").GetString();
            if (name is not null && name != "bash" && name != "read" && name != "write" && name != "edit" && name != "todo_write"
                && name != "goal_write" && name != "plan_write" && name != "web_fetch" && name != "web_search"
                && name != "job_list" && name != "job_output" && name != "job_kill" && name != "workflow"
                && name != "message_feedback" && name != "terminal_open" && name != "terminal_read" && name != "terminal_send"
                && name != "terminal_signal" && name != "terminal_close" && name != "terminal_list"
                && name != "ralph" && name != "structured_output"
                && name != "list_agents" && name != "send_message" && name != "subagent" && name != "ask_user_question"
                && name != "skill" && name != "read_image")
            {
                return name;
            }
        }
        var turnEnd = log.Split('\n').LastOrDefault(line => line.Contains("\"turn/end\"", StringComparison.Ordinal));
        if (turnEnd is not null && turnEnd.Contains("unknown tool", StringComparison.Ordinal))
        {
            var match = System.Text.RegularExpressions.Regex.Match(turnEnd, "unknown tool \\\\?\"([^\"\\\\]+)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    private static string FirstLogDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n').Where(line => line.Length > 0).ToArray();
        var actualLines = actual.Split('\n').Where(line => line.Length > 0).ToArray();
        var count = Math.Min(expectedLines.Length, actualLines.Length);
        for (var index = 0; index < count; index++)
        {
            if (expectedLines[index] != actualLines[index])
            {
                return $"log line {index}: expected {Summarize(expectedLines[index])} got {Summarize(actualLines[index])}";
            }
        }
        return $"log length: expected {expectedLines.Length} got {actualLines.Length}";
    }

    private static string Summarize(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "?";
        if (type == "reasoning-chunks" || type == "text-chunks")
        {
            var texts = root.GetProperty("data").GetProperty("texts");
            return $"{type}[{texts.GetArrayLength()} texts]";
        }
        if (type == "tool-call-chunks")
        {
            return $"tool-call-chunks[{root.GetProperty("data").GetProperty("args").GetArrayLength()}]";
        }
        if (type == "request/header")
        {
            var header = root.GetProperty("data").GetProperty("header");
            var system = header.TryGetProperty("system", out var s) ? s.GetString() : null;
            var tools = header.TryGetProperty("tools", out var ts) ? ts.GetString() : null;
            return $"request/header system={system} tools={tools}";
        }
        var brief = line.Length > 220 ? line[..220] + "…" : line;
        return $"{type} {brief}";
    }

    /// <summary>The recorded user task: the first user/message text, else the first inbox-inserted text.</summary>
    public static string? TaskFromSession(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"user/message\"", StringComparison.Ordinal)) continue;
            var message = JsonDocument.Parse(line).RootElement.GetProperty("data");
            if (message.GetProperty("source").GetProperty("kind").GetString() != "user") continue;
            var blocks = message.GetProperty("content");
            if (blocks.GetArrayLength() == 1 && blocks[0].TryGetProperty("type", out var type) && type.GetString() == "text")
            {
                return blocks[0].GetProperty("text").GetString();
            }
        }
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"agent/inbox/spliced\"", StringComparison.Ordinal)) continue;
            var data = JsonDocument.Parse(line).RootElement.GetProperty("data");
            if (!data.TryGetProperty("inserted", out var inserted)) continue;
            foreach (var message in inserted.EnumerateArray())
            {
                if (message.GetProperty("source").GetProperty("kind").GetString() != "user") continue;
                var blocks = message.GetProperty("content");
                if (blocks.GetArrayLength() == 1 && blocks[0].TryGetProperty("type", out var type) && type.GetString() == "text")
                {
                    return blocks[0].GetProperty("text").GetString();
                }
            }
        }
        return null;
    }

    /// <summary>The recorded provider/model from the first request header.</summary>
    public static (string Provider, string Model) ModelFromSession(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"request/header\"", StringComparison.Ordinal)) continue;
            var header = JsonDocument.Parse(line).RootElement.GetProperty("data").GetProperty("header");
            var config = header.GetProperty("config");
            if (config.TryGetProperty("provider", out var provider) && config.TryGetProperty("model", out var model))
            {
                return (provider.GetString()!, model.GetString()!);
            }
        }
        // A scenario whose prompt never reaches the model (e.g. a prompt-submit hook block) has
        // no request header; the replay route then goes unused, so the recorded default applies.
        return ("deepseek-official", "deepseek-v4-flash");
    }

    /// <summary>The last turn/end reason kind.</summary>
    public static string? TurnReasonKind(string log)
    {
        string? last = null;
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"turn/end\"", StringComparison.Ordinal)) continue;
            var reason = JsonDocument.Parse(line).RootElement.GetProperty("data").GetProperty("reason");
            last = reason.GetProperty("kind").GetString();
        }
        return last;
    }

    /// <summary>The final assistant text (the headless stdout projection).</summary>
    public static string FinalTextFromSession(string log)
    {
        var text = new System.Text.StringBuilder();
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"assistant/message\"", StringComparison.Ordinal)) continue;
            var message = JsonDocument.Parse(line).RootElement.GetProperty("data").GetProperty("message");
            foreach (var block in message.GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" && block.TryGetProperty("text", out var textBlock))
                {
                    text.Append(textBlock.GetString());
                }
            }
        }
        return text.ToString();
    }

    /// <summary>Reconstruct the headless stderr projection from the log (reasoning + error line).</summary>
    public static string StderrFromSession(string log)
    {
        var output = new System.Text.StringBuilder();
        var started = false;
        var open = false;
        var endsWithNewline = true;
        void Close()
        {
            if (!open) return;
            if (!endsWithNewline) output.Append('\n');
            open = false;
            endsWithNewline = true;
        }
        foreach (var line in log.Split('\n'))
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            var data = root.TryGetProperty("data", out var d) ? d : default;
            if (type == "turn/start")
            {
                Close();
                started = true;
                continue;
            }
            if (!started) continue;
            switch (type)
            {
                case "reasoning-chunks":
                    foreach (var text in data.GetProperty("texts").EnumerateArray())
                    {
                        var piece = text.GetString() ?? "";
                        if (piece.Length == 0) continue;
                        if (!open)
                        {
                            output.Append("dsh: reasoning:\n");
                            open = true;
                        }
                        output.Append(piece);
                        endsWithNewline = piece.EndsWith('\n');
                    }
                    break;
                case "text-chunks":
                case "tool-call-chunks":
                    Close();
                    break;
                case "assistant/chunk":
                    var chunk = data.GetProperty("chunk");
                    var kind = chunk.GetProperty("type").GetString();
                    switch (kind)
                    {
                        case "reasoning-delta":
                            var delta = chunk.GetProperty("text").GetString() ?? "";
                            if (delta.Length == 0) break;
                            if (!open)
                            {
                                output.Append("dsh: reasoning:\n");
                                open = true;
                            }
                            output.Append(delta);
                            endsWithNewline = delta.EndsWith('\n');
                            break;
                        case "block-start":
                            if (chunk.GetProperty("blockType").GetString() != "reasoning") Close();
                            break;
                        case "block-end":
                            if (chunk.GetProperty("block").GetProperty("type").GetString() != "reasoning") Close();
                            break;
                        case "usage":
                            break;
                        default:
                            Close();
                            break;
                    }
                    break;
            }
        }
        Close();
        if (TurnReasonKind(log) != "error") return output.ToString();
        var turnEnd = log.Split('\n').Last(line => line.Contains("\"turn/end\"", StringComparison.Ordinal));
        var reason = JsonDocument.Parse(turnEnd).RootElement.GetProperty("data").GetProperty("reason");
        var error = reason.GetProperty("error");
        var code = error.GetProperty("code").GetString();
        var message = error.GetProperty("message").GetString();
        return output.ToString() + $"dsh: {code}: {message}\n";
    }

    /// <summary>Parse the manifest fields the runner consumes.</summary>
    public static ScenarioManifest? ParseManifest(string name, string yaml)
    {
        if (!RegexMatches(yaml, @"(?m)^profile:\s*headless")) return null;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var inEnvironment = false;
        foreach (var line in yaml.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^environment:"))
            {
                inEnvironment = true;
                continue;
            }
            if (inEnvironment)
            {
                var entry = Regex.Match(line, @"^\s+([A-Za-z0-9_]+):\s*(.+)$");
                if (entry.Success)
                {
                    environment[entry.Groups[1].Value] = entry.Groups[2].Value.Trim().Trim('"');
                    continue;
                }
                inEnvironment = false;
            }
        }
        return new ScenarioManifest(
            Name: name,
            Composition: MatchValue(yaml, @"(?m)^composition:\s*(\S+)") ?? "default",
            Platform: MatchValue(yaml, @"(?m)^platform:\s*(\S+)"),
            Permission: MatchValue(yaml, @"(?m)^permission:\s*(\S+)"),
            HeaderClass: MatchValue(yaml, @"(?m)^\s+class:\s*(\S+)"),
            WorkspaceSetup: MatchValue(yaml, @"(?m)^\s+setup:\s*(\S+)"),
            WorkspaceParent: MatchValue(yaml, @"(?m)^\s+parent:\s*(\S+)"),
            WorkspaceFinal: RegexMatches(yaml, @"(?m)^\s+final:\s*true"),
            ReplayOverride: RegexMatches(yaml, @"(?m)^\s+override:\s*true"),
            InputTask: MatchValue(yaml, @"(?m)^\s+task:\s*(.+)$"),
            Environment: environment);
    }

    private static bool RegexMatches(string input, string pattern) => Regex.IsMatch(input, pattern);

    private static string? MatchValue(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Apply the per-scenario environment the .NET CLI reads instead of the scenario's own
    /// cordis patches: replay model metadata, the fs diff-basis bound, and the result-spill
    /// policy (cap from the scenario's or its composition owner's cordis.snapshot.yml; root
    /// mirrors the TS snapshotSpillRoot hash so both fixture and run paths normalize identically).
    /// </summary>
    private static void ApplyScenarioEnv(
        Dictionary<string, string> env, ScenarioManifest scenario, string model,
        string scenariosDir, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var metaJson = ModelMetadataEnvJson(scenariosDir, scenario.Name, scenario.Composition, model, compositionOwners);
        if (metaJson is not null) env["DSH_SNAPSHOT_MODEL_META"] = metaJson;
        var diffBasis = DiffBasisEnv(scenariosDir, scenario.Name, scenario.Composition, compositionOwners);
        if (diffBasis is not null) env["DSH_FS_DIFF_BASIS_MAX_BYTES"] = diffBasis;
        var spillCap = SpillCapEnv(scenariosDir, scenario.Name, scenario.Composition, compositionOwners);
        if (spillCap is not null) env["DSH_SNAPSHOT_SPILL_MAX_BYTES"] = spillCap;
        env["DSH_SNAPSHOT_SPILL_ROOT"] = SpillRoot(scenario.Name);
        var compactionMaxTokens = CompactionMaxTokensEnv(scenariosDir, scenario.Name, scenario.Composition, compositionOwners);
        if (compactionMaxTokens is not null) env["DSH_SNAPSHOT_COMPACTION_MAX_TOKENS"] = compactionMaxTokens;
        var hooks = HookConfigEnv(Path.Combine(scenariosDir, scenario.Name));
        if (hooks is not null)
        {
            foreach (var pair in hooks) env[pair.Key] = pair.Value;
        }
    }

    /// <summary>The recorded child-session logs the replay provider binds (session.1.jsonl, …), path-separator joined.</summary>
    private static string? ChildFilesEnv(string dir)
    {
        var children = new List<string>();
        for (var index = 1; ; index++)
        {
            var path = Path.Combine(dir, $"session.{index}.jsonl");
            if (!File.Exists(path)) break;
            children.Add(path);
        }
        return children.Count > 0 ? string.Join(Path.PathSeparator, children) : null;
    }

    /// <summary>
    /// The scenario's workspace hooks.json and the bridge dialect its fixture records: the .NET
    /// headless profile mounts the claude-code/codex hook bridges per scenario via
    /// DSH_HOOKS_CC_CONFIG / DSH_HOOKS_CODEX_CONFIG (the scenario's own cordis patches mount the
    /// TS hook plugins; the port reads the same files through the env channel).
    /// </summary>
    private static IReadOnlyDictionary<string, string>? HookConfigEnv(string dir)
    {
        var hooksJson = Path.Combine(dir, "workspace", "hooks.json");
        var codexHooksJson = Path.Combine(dir, "workspace", "codex-hooks.json");
        var fixture = Path.Combine(dir, "session.jsonl");
        if ((!File.Exists(hooksJson) && !File.Exists(codexHooksJson)) || !File.Exists(fixture)) return null;
        var text = File.ReadAllText(fixture);
        var dialect = text.Contains("\"dialect\":\"codex\"", StringComparison.Ordinal) ? "codex"
            : text.Contains("\"dialect\":\"claude-code\"", StringComparison.Ordinal) ? "claude-code"
            : null;
        if (dialect is null) return null;
        var path = dialect == "codex" && File.Exists(codexHooksJson) ? codexHooksJson : hooksJson;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [dialect == "codex" ? "DSH_HOOKS_CODEX_CONFIG" : "DSH_HOOKS_CC_CONFIG"] = Path.GetFullPath(path),
        };
    }

    /// <summary>The scenario dir that owns each composition (a scenario with a cordis.yml registers its composition).</summary>
    private static Dictionary<string, string> CompositionOwners(string scenariosDir)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(scenariosDir))
        {
            var manifestPath = Path.Combine(dir, "snapshot.yml");
            if (!File.Exists(manifestPath)) continue;
            var manifest = ParseManifest(Path.GetFileName(dir), File.ReadAllText(manifestPath));
            if (manifest is null || !File.Exists(Path.Combine(dir, "cordis.yml"))) continue;
            owners.TryAdd(manifest.Composition, Path.GetFileName(dir));
        }
        return owners;
    }

    /// <summary>The scenario's effective cordis.snapshot.yml: its own, else its composition owner's (the TS replay patch resolution).</summary>
    private static string? SnapshotPatchPath(string scenariosDir, string scenarioName, string composition, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var own = Path.Combine(scenariosDir, scenarioName, "cordis.snapshot.yml");
        if (File.Exists(own)) return own;
        var owner = compositionOwners.GetValueOrDefault(composition);
        var inherited = owner is null ? null : Path.Combine(scenariosDir, owner, "cordis.snapshot.yml");
        return inherited is not null && File.Exists(inherited) ? inherited : null;
    }

    /// <summary>The deterministic snapshot spill root (mirror of the TS snapshotSpillRoot): /tmp/dsh-acp-snap-&lt;sha256-9&gt;.</summary>
    private static string SpillRoot(string scenarioName)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scenarioName))).ToLowerInvariant();
        return $"/tmp/dsh-acp-snap-{hash.Substring(0, 9)}";
    }

    /// <summary>The spill-policy inline cap the scenario's (or its composition's) cordis.snapshot.yml declares.</summary>
    private static string? SpillCapEnv(string scenariosDir, string scenarioName, string composition, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var patch = SnapshotPatchPath(scenariosDir, scenarioName, composition, compositionOwners);
        if (patch is null) return null;
        foreach (var line in File.ReadAllLines(patch))
        {
            var match = Regex.Match(line, @"^\s+maxInlineBytes:\s*(\d+)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>The compaction-basic summarization cap the scenario's (or its composition's)
    /// cordis.snapshot.yml declares (the overflow summary call's maxTokens must reproduce).</summary>
    private static string? CompactionMaxTokensEnv(string scenariosDir, string scenarioName, string composition, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var patch = SnapshotPatchPath(scenariosDir, scenarioName, composition, compositionOwners);
        if (patch is null) return null;
        var inBlock = false;
        foreach (var line in File.ReadAllLines(patch))
        {
            if (Regex.IsMatch(line, @"^-\s+id:\s*compaction-basic\s*$")) inBlock = true;
            else if (line.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("- insert:", StringComparison.Ordinal)) inBlock = false;
            if (!inBlock) continue;
            var match = Regex.Match(line, @"^\s+maxTokens:\s*(\d+)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>The replay provider's per-model capability metadata declared in the scenario's
    /// <c>cordis.snapshot.yml</c> (the llm-replay <c>providers[].models[]</c> blocks) for the
    /// recorded model, as the DSH_SNAPSHOT_MODEL_META env JSON; null when the scenario declares
    /// none.
    /// </summary>
    private static string? ModelMetadataEnvJson(string scenariosDir, string scenarioName, string composition, string model, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var path = SnapshotPatchPath(scenariosDir, scenarioName, composition, compositionOwners);
        if (path is null) return null;
        var blocks = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var idMatch = Regex.Match(line, @"^\s*-\s+id:\s*([A-Za-z0-9_.-]+)");
            if (idMatch.Success)
            {
                current = idMatch.Groups[1].Value;
                blocks.TryAdd(current, new Dictionary<string, string>(StringComparer.Ordinal));
                continue;
            }
            if (current is null) continue;
            var scalar = Regex.Match(line, @"^\s+(contextWindow|defaultMaxTokens|defaultReasoningEffort):\s*(.+)$");
            if (scalar.Success)
            {
                blocks[current][scalar.Groups[1].Value] = scalar.Groups[2].Value.Trim();
                continue;
            }
            var efforts = Regex.Match(line, @"^\s+reasoningEfforts:\s*\[([^\]]*)\]");
            if (efforts.Success)
            {
                blocks[current]["reasoningEfforts"] = string.Join(",", efforts.Groups[1].Value
                    .Split(',').Select(item => item.Trim().Trim('\'')).Where(item => item.Length > 0));
                continue;
            }
            var modalities = Regex.Match(line, @"^\s+inputModalities:\s*\[([^\]]*)\]");
            if (modalities.Success)
            {
                blocks[current]["inputModalities"] = string.Join(",", modalities.Groups[1].Value
                    .Split(',').Select(item => item.Trim().Trim('\'')).Where(item => item.Length > 0));
            }
        }
        if (!blocks.TryGetValue(model, out var fields) || fields.Count == 0) return null;
        var meta = new System.Text.Json.Nodes.JsonObject();
        if (fields.TryGetValue("contextWindow", out var window) && long.TryParse(window, out var windowValue))
        {
            meta["contextWindow"] = windowValue;
        }
        if (fields.TryGetValue("defaultMaxTokens", out var tokens) && int.TryParse(tokens, out var tokensValue))
        {
            meta["defaultMaxTokens"] = tokensValue;
        }
        if (fields.TryGetValue("defaultReasoningEffort", out var effort))
        {
            meta["defaultReasoningEffort"] = effort;
        }
        if (fields.TryGetValue("reasoningEfforts", out var effortList))
        {
            meta["reasoningEfforts"] = new System.Text.Json.Nodes.JsonArray(
                effortList.Split(',').Select(item => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(item)).ToArray());
        }
        if (fields.TryGetValue("inputModalities", out var modalityList))
        {
            meta["inputModalities"] = new System.Text.Json.Nodes.JsonArray(
                modalityList.Split(',').Select(item => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(item.Trim())).ToArray());
        }
        var root = new System.Text.Json.Nodes.JsonObject { [model] = meta };
        return root.ToJsonString();
    }

    /// <summary>The fs-sandbox <c>diffBasisMaxBytes</c> the scenario's cordis patch declares (its own fs-sandbox seam is deferred in the port).</summary>
    private static string? DiffBasisEnv(string scenariosDir, string scenarioName, string composition, IReadOnlyDictionary<string, string> compositionOwners)
    {
        var patch = SnapshotPatchPath(scenariosDir, scenarioName, composition, compositionOwners);
        if (patch is null) return null;
        foreach (var line in File.ReadAllLines(patch))
        {
            var match = Regex.Match(line, @"^\s+diffBasisMaxBytes:\s*(\d+)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    private static void PrepareWorkspace(string cwd, string? setup)
    {
        switch (setup)
        {
            case null:
                return;
            case "delimiter-path":
                var dir = Path.Combine(cwd, "scope</system-reminder>");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Delimiter path snapshot instruction.\n");
                File.WriteAllText(Path.Combine(dir, "task.txt"), "delimiter path snapshot task\n");
                return;
            case "fixed-search-mtimes":
                var tree = Path.Combine(cwd, "tree");
                var files = new[]
                {
                    @"archive\a.ts", @"archive\b.ts", @"archive\c.ts", @"docs\guide.md",
                    @"src\index.ts", @"test\spec.ts", "top.txt", "notes.md",
                };
                for (var index = 0; index < files.Length; index++)
                {
                    var target = Path.Combine(tree, files[index]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "fixture\n");
                    File.SetLastWriteTimeUtc(target, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(index + 1));
                }
                return;
            case "editing-cordis-skill":
                var skillDir = Path.Combine(cwd, ".dsh", "skills", "editing-cordis-compositions");
                Directory.CreateDirectory(skillDir);
                // The recorded skill content is the shipped preset's real skill document; copy it
                // verbatim so the loader renders the recorded bytes.
                var realSkill = Path.Combine(
                    SnapshotDriver.RepoRoot(), "packages", "preset", "agent-presets", "presets",
                    "cordis", "skills", "editing-cordis-compositions", "SKILL.md");
                if (File.Exists(realSkill))
                {
                    File.Copy(realSkill, Path.Combine(skillDir, "SKILL.md"));
                }
                else
                {
                    File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# editing-cordis-compositions\n");
                }
                return;
            default:
                throw new InvalidOperationException($"unknown workspace setup {setup}");
        }
    }
}