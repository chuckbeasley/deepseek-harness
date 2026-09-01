using System.Text.Json;
using Harness.Llm;
using Harness.Llm.Replay;

namespace Harness.Snapshot.Tests;

/// <summary>Unit coverage for the recorded-session script derivation (port of the TS llm-replay specs).</summary>
public static class ReplayScriptTests
{
    private static string Fixture(string scenario) => Path.Combine(
        SnapshotDriver.RepoRoot(), "snapshots", "session", scenario, "session.jsonl");

    public static void TheFsReadFixture_DerivesTwoRecordedCalls()
    {
        var entries = ReplayScript.DeriveScriptFromFile(Fixture("fs-read"));
        Assert.Equal(2, entries.Count, "fs-read records two model calls");
        var first = AssertChunks(entries[0]);
        Assert.Equal(48, first.Count, "first call chunk count");
        Assert.True(first[0] is BlockStart { BlockType: "reasoning" }, "first chunk opens the reasoning block");
        var finish = first[^1] as Finish;
        Assert.True(finish is not null && finish.Reason is ToolCalls, "first call finishes with tool-calls");
        var usage = first.OfType<UsageChunk>().Single();
        Assert.Equal(2882, usage.Usage.InputTokens, "recorded input tokens");
        Assert.Equal(75, usage.Usage.OutputTokens, "recorded output tokens");
        Assert.Equal(29, usage.Usage.ReasoningTokens, "recorded reasoning tokens");
        var second = AssertChunks(entries[1]);
        Assert.True(second[^1] is Finish { Reason: Stop }, "second call finishes with stop");
        var text = second.OfType<TextDelta>().Select(delta => delta.Text).Aggregate("", (a, b) => a + b);
        Assert.Equal("DONE", text, "replayed visible text");
    }

    public static void PackedRows_ExpandIntoDeltaEvents()
    {
        // The fs-read fixture stores reasoning and tool-call chunks as packed rows; the parser
        // must expand them into the same delta events an unpacked recording would produce.
        var events = ReplayScript.ParseSessionLog(File.ReadAllText(Fixture("fs-read")));
        var deltas = events.OfType<RecordedChunkEvent>().ToList();
        var reasoning = deltas.Where(evt => evt.Chunk is ReasoningDelta).Select(evt => ((ReasoningDelta)evt.Chunk).Text).ToList();
        Assert.True(reasoning.Count == 66, $"fs-read records 66 reasoning deltas (30 + 36), got {reasoning.Count}");
        Assert.Equal("The", reasoning[0], "first reasoning delta text");
        Assert.Equal("\".", reasoning[^1], "last reasoning delta text (the closing quote and period)");
        var tool = deltas.OfType<RecordedChunkEvent>().Where(evt => evt.Chunk is ToolCallDelta)
            .Select(evt => (ToolCallDelta)evt.Chunk).ToList();
        Assert.True(tool.Count == 13, $"fs-read records 13 tool-call deltas, got {tool.Count}");
        Assert.Equal("call_00_hHPZCcivsIkXAGS9jTGy8417", tool[0].Id.Value, "tool call id");
        Assert.Equal("read", tool[0].Name, "tool call name");
        Assert.Equal("{\"file_path\": \"greeting.txt\"}", string.Concat(tool.Select(delta => delta.ArgumentsDelta)), "assembled arguments");
    }

    public static void OverrideDocs_ValidateLoud()
    {
        var thrown = false;
        try
        {
            ReplayOverride.ReadOverrideDoc("""[{"kind":"chunks","chunks":[{"type":"bogus"}]}]""", "override.json");
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Assert.True(thrown, "an unknown chunk type is rejected");
        var doc = ReplayOverride.ReadOverrideDoc(
            """[{"kind":"throw","chunks":[],"message":"boom","code":"E_BOOM"}]""", "override.json");
        var entry = doc.WholeScript!.Single() as ThrowEntry;
        Assert.True(entry is not null && entry.Message == "boom" && entry.Code == "E_BOOM", "throw entry round-trips");
    }

    public static void Install_ConsumptionCheck_ReportsUnderruns()
    {
        var ctx = new global::Harness.Cordis.Core.Context();
        var llm = new LlmRuntime(ctx);
        var handle = ReplayInstall.Install(llm, new ReplayConfig
        {
            File = Fixture("fs-read"),
            Provider = "deepseek-official",
        });
        var request = new GenerateOptions("deepseek-official", "deepseek-v4-flash",
            new Message[] { Messages.CreateUserMessage(new ContentBlock[] { new TextBlock("hi") }) },
            SessionId: "s1");
        var chunks = new List<StreamChunk>();
        foreach (var chunk in llm.Stream(request, CancellationToken.None).ToBlockingEnumerable())
        {
            chunks.Add(chunk);
        }
        Assert.Equal(48, chunks.Count, "the first call replays 48 chunks");
        var failed = false;
        try
        {
            handle.AssertConsumed();
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Assert.True(failed, "driving fewer calls than recorded fails the consumption check");
        handle.Dispose();
        ctx.Dispose();
    }

    private static IReadOnlyList<StreamChunk> AssertChunks(ReplayEntry entry)
    {
        var chunks = entry as ChunksEntry;
        Assert.True(chunks is not null, "derived entries are chunk streams");
        return chunks!.Chunks;
    }
}
