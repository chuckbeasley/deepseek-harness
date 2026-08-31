using System.Text.Json;

namespace Dsh.Snapshot.Tests;

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
                .Where(root => root.TryGetProperty("$type", out var type) && type.GetString() == "assistant/chunk")
                .Select(root => root.GetProperty("Chunk"))
                .ToList();
            Assert.True(chunks.Count > 0, "the replayed stream persists assistant/chunk rows");
            var usage = chunks
                .Select(chunk => chunk.TryGetProperty("usage", out var value) ? value : default)
                .FirstOrDefault(value => value.ValueKind == JsonValueKind.Object);
            Assert.True(usage.ValueKind == JsonValueKind.Object, "a usage chunk was replayed");
            Assert.Equal(2882, usage.GetProperty("InputTokens").GetInt32(), "recorded input tokens replay");
            Assert.Equal(75, usage.GetProperty("OutputTokens").GetInt32(), "recorded output tokens replay");
            var reasoning = lines
                .Where(root => root.TryGetProperty("$type", out var type) && type.GetString() == "assistant/chunk")
                .Select(root => root.GetProperty("Chunk"))
                .SelectMany(chunk => chunk.TryGetProperty("Block", out var block)
                    && block.TryGetProperty("type", out var blockType) && blockType.GetString() == "reasoning"
                    ? new[] { block.GetProperty("Text").GetString() ?? "" }
                    : Array.Empty<string>())
                .FirstOrDefault();
            Assert.True(reasoning is not null && reasoning.StartsWith("The user wants me to read", StringComparison.Ordinal),
                "the recorded reasoning text replays");
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