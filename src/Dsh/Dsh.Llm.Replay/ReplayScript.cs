using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Llm;

namespace Harness.Llm.Replay;

/// <summary>One parsed record of a recorded session log that the script derivation consumes.</summary>
internal abstract record ReplayLogEvent;

/// <summary>An <c>assistant/chunk</c> event (or one member expanded from a packed chunk row).</summary>
internal sealed record RecordedChunkEvent(int Turn, int Step, StreamChunk Chunk) : ReplayLogEvent;

/// <summary>A <c>compaction/summary</c> event; a summary explicitly marked as one local LLM-stream call becomes a stream.</summary>
internal sealed record RecordedSummaryEvent(bool LlmStreamCall, IReadOnlyList<ContentBlock>? RawOutput, TokenUsage? Usage) : ReplayLogEvent;

/// <summary>
/// Keyless snapshot-test LLM replay: derive one model-call script per recorded session from
/// <c>assistant/chunk</c> events (packed chunk rows expand back into delta events) and explicitly
/// marked local compaction calls, then bind fresh live sessions to parent/child scripts by
/// first-call order. Throw and hang cases require an explicit override because a session log
/// cannot reconstruct them alone. Port of <c>@deepseek-ai/dsh-llm-replay</c>.
/// </summary>
public static class ReplayScript
{
    /// <summary>JSON options matching the committed fixture spellings (camelCase, no nulls).</summary>
    public static readonly JsonSerializerOptions FixtureJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] PackedChunkRowTypes = { "text-chunks", "reasoning-chunks", "tool-call-chunks" };

    /// <summary>Read replay identity, ordering, and fork-seed facts from the JSONL header.</summary>
    public static (string Id, long CreatedAtMs, int SeedLength) ParseSessionHeader(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString()! : "";
            var createdAt = root.TryGetProperty("createdAt", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0L;
            var seedLength = root.TryGetProperty("seedLength", out var seed) && seed.ValueKind == JsonValueKind.Number ? seed.GetInt32() : 0;
            return (id, createdAt, seedLength);
        }
        return ("", 0L, 0);
    }

    /// <summary>
    /// Parse a session <c>.jsonl</c> buffer into its replay-relevant events. Line 0 is the session
    /// header and is skipped; packed chunk rows expand back into individual delta events so the
    /// physical fixture encoding derives the same script as its unpacked recording. Malformed
    /// lines fail loud.
    /// </summary>
    internal static List<ReplayLogEvent> ParseSessionLog(string text)
    {
        var events = new List<ReplayLogEvent>();
        var headerSkipped = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("session snapshot line must be a JSON object");
            }
            var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : null;
            if (type is not null && PackedChunkRowTypes.Contains(type))
            {
                events.AddRange(ExpandPackedRow(root));
                continue;
            }
            if (type == "compaction/summary")
            {
                var data = Property(root, "data");
                var llmStreamCall = data.TryGetProperty("llmStreamCall", out var call)
                    && call.ValueKind == JsonValueKind.True;
                IReadOnlyList<ContentBlock>? rawOutput = null;
                if (data.TryGetProperty("rawOutput", out var raw) && raw.ValueKind == JsonValueKind.Array)
                {
                    rawOutput = raw.Deserialize<List<ContentBlock>>(FixtureJson);
                }
                TokenUsage? usage = null;
                if (data.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind == JsonValueKind.Object)
                {
                    usage = usageValue.Deserialize<TokenUsage>(FixtureJson);
                }
                events.Add(new RecordedSummaryEvent(llmStreamCall, rawOutput, usage));
                continue;
            }
            if (type != "assistant/chunk") continue;
            var payload = Property(root, "data");
            var turn = PropertyInt(payload, "turn");
            var step = PropertyInt(payload, "step");
            var chunkValue = Property(payload, "chunk");
            var chunk = chunkValue.Deserialize<StreamChunk>(FixtureJson);
            if (chunk is null)
            {
                throw new InvalidOperationException("session snapshot assistant/chunk carries no chunk");
            }
            events.Add(new RecordedChunkEvent(turn, step, chunk));
        }
        return events;
    }

    private static List<ReplayLogEvent> ExpandPackedRow(JsonElement root)
    {
        var data = Property(root, "data");
        var turn = PropertyInt(data, "turn");
        var step = PropertyInt(data, "step");
        var index = PropertyInt(data, "index");
        var type = root.GetProperty("type").GetString();
        var result = new List<ReplayLogEvent>();
        if (type == "tool-call-chunks")
        {
            var id = new ToolCallId(data.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString()!
                : "");
            string? name = data.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : null;
            var args = StringArray(data, "args");
            foreach (var argument in args)
            {
                result.Add(new RecordedChunkEvent(turn, step, new ToolCallDelta(index, id, name, argument)));
            }
        }
        else
        {
            var texts = StringArray(data, "texts");
            foreach (var text in texts)
            {
                result.Add(new RecordedChunkEvent(turn, step, type == "text-chunks"
                    ? new TextDelta(index, text)
                    : new ReasoningDelta(index, text)));
            }
        }
        return result;
    }

    private static JsonElement Property(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new InvalidOperationException($"session snapshot record lacks \"{name}\"");
        }
        return value;
    }

    private static int PropertyInt(JsonElement root, string name)
    {
        var value = Property(root, name);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"session snapshot record \"{name}\" must be a number");
        }
        return value.GetInt32();
    }

    private static string[] StringArray(JsonElement root, string name)
    {
        var value = Property(root, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"session snapshot record \"{name}\" must be an array");
        }
        return value.EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    }

    /// <summary>
    /// Reconstruct the per-<c>stream()</c> replay script from a recorded session log. Splits
    /// <c>assistant/chunk</c> events at every <c>finish</c>, using turn and step changes to detect
    /// an unterminated prior call. A missing assistant terminator means the live stream threw, so
    /// derivation rejects and the scenario must provide an explicit override. Multiple calls may
    /// share one turn and step when the loop retries.
    /// </summary>
    internal static List<ReplayEntry> DeriveReplayScript(IEnumerable<ReplayLogEvent> events)
    {
        var script = new List<ReplayEntry>();
        string? currentKey = null;
        var current = new List<StreamChunk>();
        void Close(string? key, List<StreamChunk> chunks)
        {
            if (chunks.Count == 0) return;
            if (chunks[^1] is not Finish)
            {
                throw new InvalidOperationException(
                    $"llm-replay: model call {key} ended without a finish chunk (a thrown stream); "
                    + "this scenario needs a replay.override.json sidecar");
            }
            script.Add(new ChunksEntry(chunks.ToArray()));
        }
        foreach (var evt in events)
        {
            if (evt is RecordedSummaryEvent summary)
            {
                Close(currentKey, current);
                currentKey = null;
                current.Clear();
                if (summary.LlmStreamCall)
                {
                    if (summary.RawOutput is null)
                    {
                        throw new InvalidOperationException("llm-replay: compaction/summary marks an LLM stream call without rawOutput");
                    }
                    var chunks = new List<StreamChunk>();
                    for (var index = 0; index < summary.RawOutput.Count; index++)
                    {
                        var block = summary.RawOutput[index];
                        chunks.Add(new BlockStart(index, block.BlockType));
                        chunks.Add(new BlockEnd(index, block));
                    }
                    if (summary.Usage is not null) chunks.Add(new UsageChunk(summary.Usage));
                    chunks.Add(new Finish(new Stop()));
                    script.Add(new ChunksEntry(chunks));
                }
                continue;
            }
            if (evt is not RecordedChunkEvent chunkEvent) continue;
            var key = $"{chunkEvent.Turn}/{chunkEvent.Step}";
            if (current.Count > 0 && key != currentKey)
            {
                Close(currentKey, current);
                current.Clear();
            }
            if (current.Count == 0) currentKey = key;
            current.Add(chunkEvent.Chunk);
            if (chunkEvent.Chunk is Finish)
            {
                Close(currentKey, current);
                currentKey = null;
                current.Clear();
            }
        }
        Close(currentKey, current);
        return script;
    }

    /// <summary>Derive the primary script from the session JSONL, failing loud on a missing fixture.</summary>
    public static List<ReplayEntry> DeriveScriptFromFile(string file)
    {
        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"llm-replay: fixture not found: {file}");
        }
        return DeriveReplayScript(ParseSessionLog(File.ReadAllText(file)));
    }

    /// <summary>
    /// Load the primary and child scripts in bind order. Child derivation begins at
    /// <c>seedLength</c> so inherited parent chunks are never replayed as child calls.
    /// </summary>
    public static List<SessionScript> LoadSessionScripts(ReplayConfig config)
    {
        var primaryEntries = LoadPrimaryScript(config);
        var primaryHeader = File.Exists(config.File)
            ? ParseSessionHeader(File.ReadAllText(config.File))
            : (Id: "", CreatedAtMs: 0L, SeedLength: 0);
        var scripts = new List<SessionScript>
        {
            new(primaryHeader.Id, primaryHeader.CreatedAtMs, primaryEntries, Primary: true),
        };
        foreach (var childFile in config.ChildFiles ?? Array.Empty<string>())
        {
            if (!File.Exists(childFile))
            {
                throw new InvalidOperationException($"llm-replay: child fixture not found: {childFile}");
            }
            var text = File.ReadAllText(childFile);
            var header = ParseSessionHeader(text);
            var ownEvents = ParseSessionLog(text).Skip(header.SeedLength).ToList();
            scripts.Add(new SessionScript(header.Id, header.CreatedAtMs, DeriveReplayScript(ownEvents), Primary: false));
        }
        // Synchronous children start in creation order; the id only stabilizes timestamp ties.
        return scripts
            .Take(1)
            .Concat(scripts.Skip(1).OrderBy(child => child.CreatedAtMs).ThenBy(child => child.RecordedId, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>Load the primary session's replay script: override sidecar when present, else the JSONL-derived script.</summary>
    public static List<ReplayEntry> LoadPrimaryScript(ReplayConfig config)
    {
        if (config.OverrideFile is not null && File.Exists(config.OverrideFile))
        {
            var doc = ReplayOverride.ReadOverrideDoc(File.ReadAllText(config.OverrideFile), config.OverrideFile);
            if (doc.WholeScript is not null) return doc.WholeScript.ToList();
            var script = DeriveScriptFromFile(config.File);
            var derivedLength = script.Count;
            var seenIndexes = new HashSet<int>();
            foreach (var patch in doc.Patches!)
            {
                if (patch.At > derivedLength)
                {
                    throw new InvalidOperationException(
                        $"llm-replay: override patch index {patch.At} out of range "
                        + $"(derived script has {derivedLength} call(s); == length appends): {config.OverrideFile}");
                }
                if (!seenIndexes.Add(patch.At))
                {
                    throw new InvalidOperationException($"llm-replay: duplicate override patch index {patch.At}: {config.OverrideFile}");
                }
                script[patch.At] = patch.Entry;
            }
            return script;
        }
        return DeriveScriptFromFile(config.File);
    }
}