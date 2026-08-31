using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Snapshot.Tests;

/// <summary>One run's volatile values the normalizers recognize.</summary>
public sealed record NormalizeContext(string Cwd, IReadOnlyList<string> SessionIds);

/// <summary>
/// Pure session-log normalizers (port of the TS session-snapshot normalize + identity modules):
/// they scrub the run cwd, session ids, and message ids with typed relationship-preserving
/// tokens, tokenize request-header bulk, zero volatile clocks, and project the log to the
/// committed-fixture spelling (packed chunk rows, no persistence envelopes).
/// </summary>
public static class SnapshotNormalizer
{
    public const string SessionToken = "{{sessionId}}";
    public const string MessageToken = "{{messageId}}";
    public const string CwdToken = "{{cwd}}";
    public const string SystemToken = "{{system}}";
    public const string ToolsToken = "{{tools}}";

    private static readonly Regex UuidRegex = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTokenRegex = new(@"^\{\{(session|message|approval|workflow|command|rpc|retry|id):([1-9]\d*)\}\}$");
    private static readonly Regex IdKeyRegex = new(@"(?:^id$|Id$|Ids$)");
    private static readonly Regex CwdRootedPathRegex = new(@"\{\{cwd\}\}(?:[\\/][^\s<>""'`]+)+");
    private static readonly Regex PathTagRegex = new(@"(<path>)([^<]*)(</path>)");
    private static readonly string[] PackedRowTypes = { "text-chunks", "reasoning-chunks", "tool-call-chunks" };

    private static readonly char[] PathTextBoundary = { ' ', '\t', '<', '>', '"', '\'', '`', '(', ')', '[', ']', '{', '}', ',', ';', ':', '!', '?', '=' };

    private static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    private static List<JsonObject> ParseRecords(string rawLog)
    {
        var records = new List<JsonObject>();
        foreach (var line in rawLog.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var node = JsonNode.Parse(line) as JsonObject
                ?? throw new InvalidOperationException($"session snapshot line must be a JSON object: {line[..Math.Min(80, line.Length)]}");
            records.Add(node);
        }
        return records;
    }

    private static bool IsPackedRow(JsonObject record)
        => record["type"]?.GetValue<string>() is { } type && PackedRowTypes.Contains(type);

    private static void OmitFixtureEnvelope(JsonObject record)
    {
        record.Remove("seq");
        record.Remove("time");
        record.Remove("seq0");
        record.Remove("time0");
    }

    private static string ReplaceCwd(string value, string cwd, string replacement)
    {
        if (cwd.Length == 0 || !value.Contains(cwd, StringComparison.Ordinal)) return value;
        var output = new System.Text.StringBuilder();
        var cursor = 0;
        while (cursor < value.Length)
        {
            var match = value.IndexOf(cwd, cursor, StringComparison.Ordinal);
            if (match < 0)
            {
                output.Append(value, cursor, value.Length - cursor);
                break;
            }
            var end = match + cwd.Length;
            var before = match == 0 ? '\0' : value[match - 1];
            var after = end >= value.Length ? '\0' : value[end];
            var afterPunctuation = end + 1 >= value.Length ? '\0' : value[end + 1];
            var startsAtBoundary = match == 0 || PathTextBoundary.Contains(before);
            var endsAtBoundary = end >= value.Length || after == '/' || after == '\\'
                || PathTextBoundary.Contains(after)
                || after == '.' && (end + 1 >= value.Length || PathTextBoundary.Contains(afterPunctuation));
            if (startsAtBoundary && endsAtBoundary)
            {
                output.Append(value, cursor, match - cursor).Append(replacement);
            }
            else
            {
                output.Append(value, cursor, end - cursor);
            }
            cursor = end;
        }
        return output.ToString();
    }

    /// <summary>Replace the run cwd and any stray UUID with stable tokens (legacy identity mode).</summary>
    private static string ScrubString(string value, NormalizeContext ctx, bool preserveIdentity)
    {
        var output = ReplaceCwd(value, ctx.Cwd, CwdToken).Replace("/private" + CwdToken, CwdToken);
        output = CwdRootedPathRegex.Replace(output, match => match.Value.Replace('\\', '/'));
        output = PathTagRegex.Replace(output, match => match.Groups[1].Value + match.Groups[2].Value.Replace('\\', '/') + match.Groups[3].Value);
        if (!preserveIdentity)
        {
            foreach (var id in ctx.SessionIds) output = output.Replace(id, SessionToken);
            output = UuidRegex.Replace(output, SessionToken);
        }
        return output;
    }

    private static JsonNode? ScrubValue(JsonNode? value, NormalizeContext ctx, bool preserveIdentity, string? key = null)
    {
        switch (value)
        {
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                if (!preserveIdentity && key == "messageId") return JsonValue.Create(MessageToken);
                var scrubbed = ScrubString(text, ctx, preserveIdentity);
                return JsonValue.Create(scrubbed);
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array) items.Add(ScrubValue(item, ctx, preserveIdentity));
                return items;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var pair in obj)
                {
                    result[pair.Key] = ScrubValue(pair.Value, ctx, preserveIdentity, pair.Key);
                }
                return result;
            default:
                return value?.DeepClone();
        }
    }

    /// <summary>Relationship-preserving identity redaction (port of the TS redactSessionSnapshotIds).</summary>
    public static string[] RedactSessionSnapshotIds(IReadOnlyList<string> logs)
    {
        var parsed = logs.Select(ParseRecords).ToList();
        var tokenByValue = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextByKind = new Dictionary<string, int>(StringComparer.Ordinal);

        void Claim(string? value, string kind, bool always)
        {
            if (value is not { Length: > 0 } || tokenByValue.ContainsKey(value)) return;
            if (!always && !(UuidRegex.IsMatch(value) || value is "{{sessionId}}" or "{{messageId}}" || CanonicalTokenRegex.IsMatch(value))) return;
            var canonical = CanonicalTokenRegex.Match(value);
            if (canonical.Success)
            {
                var canonicalKind = canonical.Groups[1].Value;
                var ordinal = int.Parse(canonical.Groups[2].Value);
                nextByKind[canonicalKind] = Math.Max(nextByKind.GetValueOrDefault(canonicalKind), ordinal);
                tokenByValue[value] = value;
                return;
            }
            var next = nextByKind.GetValueOrDefault(kind) + 1;
            nextByKind[kind] = next;
            tokenByValue[value] = $"{{{{{kind}:{next}}}}}";
        }

        foreach (var log in parsed)
        {
            var header = log.Count > 0 ? log[0] : null;
            if (header?["type"]?.GetValue<string>() == "session")
            {
                Claim(header["id"]?.GetValue<string>(), "session", always: true);
            }
        }

        void Collect(JsonNode? value, string? recordType)
        {
            switch (value)
            {
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                    foreach (Match match in Regex.Matches(text, @"\bas message ([0-9a-f-]{36})\b", RegexOptions.IgnoreCase))
                    {
                        Claim(match.Groups[1].Value, "message", always: false);
                    }
                    foreach (Match match in Regex.Matches(text, @"\bAnonymous user: ([0-9a-f-]{36})\b", RegexOptions.IgnoreCase))
                    {
                        Claim(match.Groups[1].Value, "id", always: false);
                    }
                    return;
                case JsonArray array:
                    foreach (var item in array) Collect(item, recordType);
                    return;
                case JsonObject obj:
                    if (obj["id"] is JsonValue idNode && idNode.TryGetValue<string>(out var messageId)
                        && obj["role"]?.GetValue<string>() is { }
                        && obj["content"] is JsonArray
                        && obj["source"] is JsonObject)
                    {
                        Claim(messageId, "message", always: false);
                    }
                    foreach (var pair in obj)
                    {
                        var child = pair.Value;

                        if (child is JsonValue idValue && idValue.TryGetValue<string>(out var idText))
                        {
                            if (recordType is "approval/asked" or "approval/decided")
                            {
                                if (pair.Key == "id") Claim(idText, "approval", always: false);
                            }
                            else if (pair.Key == "commandId")
                            {
                                Claim(idText, "command", always: true);
                            }
                            else if (pair.Key == "rpcId")
                            {
                                Claim(idText, "rpc", always: true);
                            }
                            else if (pair.Key == "retryId")
                            {
                                Claim(idText, "retry", always: false);
                            }
                            else if (pair.Key == "runId")
                            {
                                Claim(idText, "workflow", always: false);
                            }
                            else if (IdKeyRegex.IsMatch(pair.Key))
                            {
                                Claim(idText, "id", always: false);
                            }
                        }
                        Collect(child, recordType);
                    }
                    return;
            }
        }
        foreach (var log in parsed)
        {
            foreach (var record in log) Collect(record, record["type"]?.GetValue<string>());
        }

        var replacements = tokenByValue
            .OrderByDescending(pair => pair.Key.Length)
            .ToArray();

        JsonNode? Replace(JsonNode? value)
        {
            switch (value)
            {
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                    if (tokenByValue.TryGetValue(text, out var exact)) return JsonValue.Create(exact);
                    var output = text;
                    foreach (var pair in replacements) output = output.Replace(pair.Key, pair.Value);
                    return JsonValue.Create(output);
                case JsonArray array:
                    var items = new JsonArray();
                    foreach (var item in array) items.Add(Replace(item));
                    return items;
                case JsonObject obj:
                    var result = new JsonObject();
                    foreach (var pair in obj) result[pair.Key] = Replace(pair.Value);
                    return result;
                default:
                    return value?.DeepClone();
            }
        }

        return parsed.Select(log => string.Join('\n', log.Select(record => Replace(record)!.ToJsonString()))).ToArray();
    }

    /// <summary>Normalize a session log: zero volatile clocks, decode provenance, scrub volatile strings (port of normalizeSessionLog).</summary>
    public static string NormalizeSessionLog(string rawLog, NormalizeContext ctx)
    {
        var records = ParseRecords(rawLog);
        var output = new List<string>();
        foreach (var record in records)
        {
            if (record["type"]?.GetValue<string>() == "session")
            {
                if (record.ContainsKey("createdAt")) record["createdAt"] = 0;
            }
            else if (IsPackedRow(record))
            {
                if (record.ContainsKey("time0")) record["time0"] = 0;
                if (record["data"] is JsonObject data && data["dt"] is JsonArray dt)
                {
                    for (var index = 0; index < dt.Count; index++) dt[index] = 0;
                }
            }
            else if (record.ContainsKey("time"))
            {
                record["time"] = 0;
            }
            if (record["type"]?.GetValue<string>() == "hook/result" && record["data"] is JsonObject hookData)
            {
                if (hookData.ContainsKey("durationMs")) hookData["durationMs"] = 0;
            }
            if (record["type"]?.GetValue<string>() == "goal/change" && record["data"] is JsonObject goalData)
            {
                if (goalData.ContainsKey("createdAt")) goalData["createdAt"] = 0;
                if (goalData.ContainsKey("updatedAt")) goalData["updatedAt"] = 0;
            }
            if (record.ContainsKey("sourceEventSeqs"))
            {
                record["sourceEventSeqs"] = DecodeSeqRanges(record["sourceEventSeqs"]);
            }
            output.Add(ScrubValue(record, ctx, preserveIdentity: true)!.ToJsonString());
        }
        return string.Join('\n', output);
    }

    /// <summary>Decode one storage-form source-sequence array into a flat list (port of decodeSeqRanges).</summary>
    public static JsonArray DecodeSeqRanges(JsonNode? value)
    {
        var array = value as JsonArray ?? throw new InvalidOperationException("sourceEventSeqs must be an array");
        var decoded = new JsonArray();
        foreach (var entry in array)
        {
            if (entry is JsonValue)
            {
                decoded.Add(entry.DeepClone());
                continue;
            }
            if (entry is not JsonArray range || range.Count != 2 || range[0] is not JsonValue || range[1] is not JsonValue)
            {
                throw new InvalidOperationException("sourceEventSeqs range entries must be [start, end] pairs");
            }
            var start = range[0]!.GetValue<long>();
            var end = range[1]!.GetValue<long>();
            for (var seq = start; seq <= end; seq++) decoded.Add(seq);
        }
        return decoded;
    }

    /// <summary>Project a log: tokenize request-header bulk and strip the persistence envelope (port of scrubSessionSnapshot).</summary>
    public static string ScrubSessionSnapshot(string rawLog)
    {
        var scrubbed = ScrubRequestHeaders(rawLog);
        var records = ParseRecords(scrubbed);
        var lines = scrubbed.Split('\n').Where(line => line.Trim().Length > 0).ToArray();
        var output = new List<string>();
        var recordIndex = 0;
        foreach (var line in lines)
        {
            var record = records[recordIndex++];
            if (recordIndex == 1)
            {
                if (record["type"]?.GetValue<string>() != "session")
                {
                    throw new InvalidOperationException("session snapshot must start with a session header");
                }
                output.Add(line);
                continue;
            }
            OmitFixtureEnvelope(record);
            output.Add(record.ToJsonString());
        }
        return string.Join('\n', output);
    }

    /// <summary>Replace system-prompt and tool-schema content in request headers with tokens (port of scrubRequestHeaders).</summary>
    public static string ScrubRequestHeaders(string rawLog)
    {
        var lines = rawLog.Split('\n');
        var output = new List<string>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                output.Add(line);
                continue;
            }
            var record = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("line must be a JSON object");
            if (record["type"]?.GetValue<string>() == "request/header" && record["data"] is JsonObject data && data["header"] is JsonObject header)
            {
                var touched = false;
                if (header.ContainsKey("system")) { header["system"] = SystemToken; touched = true; }
                if (header.ContainsKey("tools")) { header["tools"] = ToolsToken; touched = true; }
                output.Add(touched ? record.ToJsonString() : line);
            }
            else
            {
                output.Add(line);
            }
        }
        return string.Join('\n', output);
    }

    /// <summary>
    /// Repack a projected log into the canonical committed layout: expand packed rows, assign
    /// sequential seqs, re-pack delta runs, and strip the persistence envelope (port of
    /// repackSessionSnapshot).
    /// </summary>
    public static string RepackSessionSnapshot(string rawLog)
    {
        var records = ParseRecords(rawLog);
        var header = records[0];
        var events = new List<JsonObject>();
        var nextSeq = 0L;
        foreach (var record in records.Skip(1))
        {
            if (IsPackedRow(record))
            {
                foreach (var member in ExpandPackedRow(record, nextSeq))
                {
                    events.Add(member);
                    nextSeq++;
                }
            }
            else
            {
                var clone = (JsonObject)record.DeepClone();
                clone["seq"] = nextSeq;
                clone["time"] = 0;
                events.Add(clone);
                nextSeq++;
            }
        }
        var packed = PackChunkRuns(events);
        var body = packed.Select(row =>
        {
            var projected = (JsonObject)row.DeepClone();
            OmitFixtureEnvelope(projected);
            return projected.ToJsonString();
        });
        return string.Join('\n', new[] { header.ToJsonString() }.Concat(body));
    }

    /// <summary>Expand one packed row into its delta events (port of expandRow).</summary>
    private static List<JsonObject> ExpandPackedRow(JsonObject row, long seq0)
    {
        var data = row["data"] as JsonObject ?? throw new InvalidOperationException("packed row data must be an object");
        var type = row["type"]!.GetValue<string>();
        var members = data[type == "tool-call-chunks" ? "args" : "texts"] as JsonArray
            ?? throw new InvalidOperationException("packed row payload missing");
        var events = new List<JsonObject>();
        for (var index = 0; index < members.Count; index++)
        {
            var chunk = new JsonObject { ["index"] = (long)data["index"]!.GetValue<int>() };
            if (type == "tool-call-chunks")
            {
                chunk["type"] = "tool-call-delta";
                chunk["id"] = data["id"]!.DeepClone();
                if (data.ContainsKey("name")) chunk["name"] = data["name"]!.DeepClone();
                chunk["argumentsDelta"] = members[index]!.DeepClone();
            }
            else
            {
                chunk["type"] = type == "text-chunks" ? "text-delta" : "reasoning-delta";
                chunk["text"] = members[index]!.DeepClone();
            }
            var evt = new JsonObject
            {
                ["type"] = "assistant/chunk",
                ["seq"] = seq0 + index,
                ["time"] = 0,
                ["data"] = new JsonObject
                {
                    ["turn"] = data["turn"]!.DeepClone(),
                    ["step"] = data["step"]!.DeepClone(),
                    ["chunk"] = chunk,
                },
            };
            events.Add(evt);
        }
        return events;
    }

    private static string? ClassifyDelta(JsonObject evt)
    {
        if (evt["type"]?.GetValue<string>() != "assistant/chunk" || evt["seq"] is not JsonValue || evt["time"] is not JsonValue) return null;
        if (evt["data"] is not JsonObject data || data["chunk"] is not JsonObject chunk
            || data["turn"] is not JsonValue || data["step"] is not JsonValue || chunk["index"] is not JsonValue)
        {
            return null;
        }
        var kind = chunk["type"]?.GetValue<string>();
        switch (kind)
        {
            case "text-delta":
            case "reasoning-delta":
                return chunk["text"] is JsonValue ? kind : null;
            case "tool-call-delta":
                return chunk["id"] is JsonValue && chunk["argumentsDelta"] is JsonValue ? kind : null;
            default:
                return null;
        }
    }


    private static bool DeepEquals(JsonNode? left, JsonNode? right)
        => left is null ? right is null : right is not null && left.ToJsonString() == right.ToJsonString();

    /// <summary>Pack runs of at least three consecutive whitelisted same-kind deltas (port of packChunkRuns).</summary>
    public static List<JsonObject> PackChunkRuns(List<JsonObject> events)
    {
        const int minRun = 3;
        var output = new List<JsonObject>();
        string? kind = null;
        var run = new List<JsonObject>();
        void Flush()
        {
            if (kind is not null && run.Count >= minRun)
            {
                output.Add(BuildRow(kind, run));
            }
            else
            {
                output.AddRange(run);
            }
            kind = null;
            run.Clear();
        }
        foreach (var evt in events)
        {
            var k = ClassifyDelta(evt);
            if (k is null)
            {
                Flush();
                output.Add(evt);
                continue;
            }
            var last = run.Count > 0 ? run[^1] : null;
            if (k == kind && last is not null && Continues(last, evt, k))
            {
                run.Add(evt);
                continue;
            }
            Flush();
            kind = k;
            run.Add(evt);
        }
        Flush();
        return output;
    }

    private static bool Continues(JsonObject prev, JsonObject next, string kind)
    {
        if (next["seq"]!.GetValue<long>() != prev["seq"]!.GetValue<long>() + 1) return false;
        var prevData = prev["data"] as JsonObject;
        var nextData = next["data"] as JsonObject;
        var prevChunk = prevData!["chunk"] as JsonObject;
        var nextChunk = nextData!["chunk"] as JsonObject;
        if (!DeepEquals(prevData!["turn"], nextData!["turn"]) || !DeepEquals(prevData["step"], nextData["step"])) return false;
        if (!DeepEquals(prevChunk!["index"], nextChunk!["index"])) return false;
        if (kind != "tool-call-delta") return true;
        return DeepEquals(prevChunk["id"], nextChunk["id"])
            && prevChunk.ContainsKey("name") == nextChunk.ContainsKey("name")
            && (!prevChunk.ContainsKey("name") || DeepEquals(prevChunk["name"], nextChunk["name"]));
    }

    private static JsonObject BuildRow(string kind, List<JsonObject> run)
    {
        var first = run[0];
        var firstData = first["data"] as JsonObject;
        var firstChunk = firstData!["chunk"] as JsonObject;
        var dt = new JsonArray();
        for (var index = 1; index < run.Count; index++)
        {
            dt.Add(0L);
        }
        var data = new JsonObject
        {
            ["turn"] = firstData!["turn"]!.DeepClone(),
            ["step"] = firstData["step"]!.DeepClone(),
            ["index"] = firstChunk!["index"]!.DeepClone(),
            ["dt"] = dt,
        };
        if (kind == "tool-call-delta")
        {
            var args = new JsonArray();
            foreach (var evt in run)
            {
                args.Add(((evt["data"] as JsonObject)!["chunk"] as JsonObject)!["argumentsDelta"]!.DeepClone());
            }
            data["id"] = firstChunk["id"]!.DeepClone();
            if (firstChunk.ContainsKey("name")) data["name"] = firstChunk["name"]!.DeepClone();
            data["args"] = args;
            return new JsonObject
            {
                ["type"] = "tool-call-chunks",
                ["seq0"] = first["seq"]!.DeepClone(),
                ["time0"] = 0L,
                ["data"] = data,
            };
        }
        var texts = new JsonArray();
        foreach (var evt in run)
        {
            texts.Add(((evt["data"] as JsonObject)!["chunk"] as JsonObject)!["text"]!.DeepClone());
        }
        data["texts"] = texts;
        return new JsonObject
        {
            ["type"] = kind == "text-delta" ? "text-chunks" : "reasoning-chunks",
            ["seq0"] = first["seq"]!.DeepClone(),
            ["time0"] = 0L,
            ["data"] = data,
        };
    }

    /// <summary>Normalize one scenario's logs for committed comparison (port of normalizeSessionSnapshots).</summary>
    public static string[] NormalizeSessionSnapshots(IReadOnlyList<string> rawLogs, NormalizeContext ctx)
    {
        var redacted = RedactSessionSnapshotIds(rawLogs);
        return redacted.Select(log => RepackSessionSnapshot(ScrubSessionSnapshot(NormalizeSessionLog(log, ctx)))).ToArray();
    }
}