using System.Text.Json;

namespace Harness.Snapshot.Tests;

/// <summary>
/// End-to-end coverage: the real dsh CLI subprocess replays the recorded fixture through the
/// snapshot overlay (the replay row serves the recorded streams; the session log persists them).
/// </summary>
public static class EndToEndTests
{
    private static string SessionFixture(string scenario) => Path.Combine(
        SnapshotDriver.RepoRoot(), "snapshots", "session", scenario, "session.jsonl");

    private static string WorkspaceDir(string scenario) => Path.Combine(
        SnapshotDriver.RepoRoot(), "snapshots", "session", scenario, "workspace");

    public static void TheHeadlessProfile_ReplaysTheRecordedStream()
    {
        var (home, cwd) = SnapshotDriver.CreateRunDirs();
        try
        {
            SnapshotDriver.SeedWorkspace(cwd, WorkspaceDir("fs-read"));
            var result = SnapshotDriver.RunHeadless(
                home,
                cwd,
                "Use the read tool (NOT bash) to read the file greeting.txt in the current directory, then reply with exactly the single word DONE.",
                SessionFixture("fs-read"),
                provider: "deepseek-official",
                model: "deepseek-v4-flash");
            var log = SnapshotDriver.HarvestSessionLog(home)
                ?? throw new AssertionException("the headless run persisted no session log");
            // The recorded stream must have been served: the persisted log carries the recorded
            // usage accounting and the recorded reasoning text.
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToList();
            var chunks = lines
                .Where(root => root.TryGetProperty("type", out var type) && type.GetString() == "assistant/chunk")
                .Select(root => root.GetProperty("data").GetProperty("chunk"))
                .ToList();
            Assert.True(chunks.Count > 0, "the replayed stream persists assistant/chunk rows");
            var usage = chunks
                .Select(chunk => chunk.TryGetProperty("usage", out var value) ? value : default)
                .FirstOrDefault(value => value.ValueKind == JsonValueKind.Object);
            Assert.True(usage.ValueKind == JsonValueKind.Object, "a usage chunk was replayed");
            Assert.Equal(2882, usage.GetProperty("inputTokens").GetInt32(), "recorded input tokens replay");
            Assert.Equal(75, usage.GetProperty("outputTokens").GetInt32(), "recorded output tokens replay");
            var reasoning = lines
                .Where(root => root.TryGetProperty("type", out var type) && type.GetString() == "assistant/chunk")
                .Select(root => root.GetProperty("data").GetProperty("chunk"))
                .SelectMany(chunk => chunk.TryGetProperty("block", out var block)
                    && block.TryGetProperty("type", out var blockType) && blockType.GetString() == "reasoning"
                    ? new[] { block.GetProperty("text").GetString() ?? "" }
                    : Array.Empty<string>())
                .FirstOrDefault();
            Assert.True(reasoning is not null && reasoning.StartsWith("The user wants me to read", StringComparison.Ordinal),
                "the recorded reasoning text replays");
            // The persisted header carries the run cwd (the TS header shape).
            var header = lines[0];
            Assert.Equal("session-headless", header.GetProperty("id").GetString(), "headless session id");
            Assert.Equal(0, header.GetProperty("delegationDepth").GetInt32(), "top-level delegation depth");
            Assert.True(header.TryGetProperty("cwd", out var headerCwd) && headerCwd.GetString() is { Length: > 0 },
                "the header carries the workspace cwd");
            // The recorded policy baseline opens the log, in the recorded order.
            var types = lines.Select(root => root.GetProperty("type").GetString()).ToList();
            Assert.Equal("permission/preset", types[1], "permission preset baseline");
            Assert.Equal("sandbox/mode", types[2], "sandbox mode baseline");
            Assert.Equal("approval/policy", types[3], "approval policy baseline");
            Assert.Equal("danger-full-access", lines[1].GetProperty("data").GetProperty("preset").GetString(), "preset value");
            Assert.Equal("danger-full-access", lines[2].GetProperty("data").GetProperty("mode").GetString(), "mode value");
            Assert.Equal("never", lines[3].GetProperty("data").GetProperty("policy").GetString(), "policy value");
            // The inbox records its insert and consume splices around turn/start.
            Assert.True(types.Contains("agent/inbox/spliced", StringComparer.Ordinal), "inbox splices are durable");
            // The turn completes: the recorded stream replays and the read tool resolves.
            Assert.Equal("completed", lines.Last().GetProperty("data").GetProperty("reason").GetProperty("kind").GetString(),
                "turn completes");
            Assert.True(types.Contains("tool/result", StringComparer.Ordinal), "the read tool produced a durable result");
            var toolResult = lines.First(root => root.GetProperty("type").GetString() == "tool/result");
            Assert.True(toolResult.GetProperty("data").TryGetProperty("meta", out var meta)
                && meta.GetProperty("totalLines").GetInt32() == 1 && meta.GetProperty("lines").GetArrayLength() == 1,
                "the read meta carries the TS {path, offset, lines, totalLines} shape");
            // The runtime-context snapshot message carries the TS sections.
            var context = lines.First(root => root.GetProperty("type").GetString() == "user/message"
                && root.GetProperty("data").GetProperty("source").TryGetProperty("form", out var form)
                && form.GetString() == "snapshot");
            Assert.Equal("@deepseek-ai/dsh-system-prompt", context.GetProperty("data").GetProperty("source").GetProperty("plugin").GetString(),
                "the snapshot source names the system-prompt plugin");
            // The fixture must be fully consumed by the end of the run: no underrun diagnostic.
            Assert.DoesNotContain("fixture not fully consumed", result.Stderr, "no fixture underrun");
        }
        finally
        {
            var root = Path.GetDirectoryName(home);
            if (root is not null) TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup; a leftover temp dir is harmless
        }
    }
}