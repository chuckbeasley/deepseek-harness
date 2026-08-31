using System.Text;
using System.Text.Json;
using Dsh.Session;

namespace Dsh.Session.Persistence;

/// <summary>
/// The TS-compatible session-log storage spelling: one event per JSON line as
/// <c>{"type","seq","time","data",…}</c> with camelCase payloads, canonical empty optionals
/// absent, provenance (<c>sourceEventSeqs</c>) range-encoded, and the message-surface fields
/// (<c>surfaceOp</c>) at the record level. The committed snapshot fixtures are this shape
/// (minus the synthesized seq/time), so the .NET log is byte-comparable after normalization.
/// </summary>
public static class SessionLogFormat
{
    /// <summary>Serialize one event as its storage line (no trailing newline).</summary>
    public static string EventLine(SessionEvent evt)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", evt.Type);
            writer.WriteNumber("seq", evt.Seq);
            writer.WriteNumber("time", evt.TimeMs);
            writer.WritePropertyName("data");
            WriteData(writer, evt);
            if (SourceEventSeqsOf(evt) is { } seqs)
            {
                writer.WritePropertyName("sourceEventSeqs");
                JsonSerializer.Serialize(writer, EncodeSeqRanges(seqs), SessionEventTypes.CreateSerializerOptions());
            }
            if (SurfaceOpOf(evt) is { } surface)
            {
                writer.WriteString("surfaceOp", SurfaceOpName(surface));
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<long>? SourceEventSeqsOf(SessionEvent evt) => evt switch
    {
        UserMessageEvent user => user.SourceEventSeqs,
        AssistantMessageEvent assistant => assistant.SourceEventSeqs,
        ToolResultEvent tool => tool.SourceEventSeqs,
        _ => null,
    };

    private static SurfaceOp? SurfaceOpOf(SessionEvent evt) => evt switch
    {
        UserMessageEvent user => user.SurfaceOp,
        AssistantMessageEvent assistant => assistant.SurfaceOp,
        ToolResultEvent tool => tool.SurfaceOp,
        _ => null,
    };

    private static void WriteData(Utf8JsonWriter writer, SessionEvent evt)
    {
        if (evt is UserMessageEvent user)
        {
            // The TS user/message payload IS the message object (no wrapper).
            JsonSerializer.Serialize(writer, user.Message, user.Message.GetType(), SessionEventTypes.CreatePayloadOptions());
            return;
        }
        JsonSerializer.Serialize(writer, evt, evt.GetType(), SessionEventTypes.CreatePayloadOptions());
    }

    private static string SurfaceOpName(SurfaceOp surface) => surface switch
    {
        SurfaceOp.Append => "append",
        _ => throw new InvalidOperationException($"unknown surfaceOp {surface}"),
    };

    /// <summary>
    /// Parse one storage line back into its event: the envelope fields are restored onto the
    /// deserialized payload (seq = the seq = log-length contract is preserved for resume).
    /// </summary>
    public static SessionEvent ParseEventLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"corrupt session log line: {Truncate(line)}");
        }
        var seq = root.TryGetProperty("seq", out var seqValue) && seqValue.ValueKind == JsonValueKind.Number
            ? seqValue.GetInt64()
            : -1L;
        var time = root.TryGetProperty("time", out var timeValue) && timeValue.ValueKind == JsonValueKind.Number
            ? timeValue.GetInt64()
            : 0L;
        var typeName = type.GetString()!;
        using var payload = JsonDocument.Parse(data.GetRawText());
        using var rebuilt = new MemoryStream();
        using (var writer = new Utf8JsonWriter(rebuilt))
        {
            writer.WriteStartObject();
            writer.WriteString("$type", typeName);
            if (seq >= 0)
            {
                writer.WriteString("id", $"evt-{seq}");
                writer.WriteNumber("seq", seq);
                writer.WriteNumber("timeMs", time);
            }
            // The TS user/message payload IS the message object; the .NET record carries it as
            // one Message property, so the reader re-wraps it.
            if (typeName == "user/message")
            {
                writer.WritePropertyName("message");
                payload.RootElement.WriteTo(writer);
            }
            else
            {
                foreach (var property in payload.RootElement.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            }
            if (root.TryGetProperty("sourceEventSeqs", out var seqs) && seqs.ValueKind == JsonValueKind.Array)
            {
                var decoded = DecodeSeqRanges(seqs, seq);
                writer.WritePropertyName("sourceEventSeqs");
                JsonSerializer.Serialize(writer, decoded, SessionEventTypes.CreateSerializerOptions());
            }
            if (root.TryGetProperty("surfaceOp", out var surface) && surface.ValueKind == JsonValueKind.String)
            {
                writer.WritePropertyName("surfaceOp");
                surface.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var evt = JsonSerializer.Deserialize<SessionEvent>(
            Encoding.UTF8.GetString(rebuilt.ToArray()), SessionEventTypes.CreateSerializerOptions())
            ?? throw new JsonException($"corrupt session log line: {Truncate(line)}");
        if (seq >= 0 && evt.Seq != seq)
        {
            throw new JsonException(
                $"corrupt session log line: payload seq {evt.Seq} does not match envelope seq {seq}");
        }
        return evt;
    }

    /// <summary>
    /// Expand one storage-form source-sequence array: numbers pass through; <c>[start, end]</c>
    /// inclusive pairs expand (the TS <c>decodeSeqRanges</c> contract).
    /// </summary>
    internal static long[] DecodeSeqRanges(JsonElement value, long maxEntries)
    {
        var decoded = new List<long>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Number)
            {
                var seq = entry.GetInt64();
                if (seq < 0) throw new JsonException("sourceEventSeqs must contain non-negative integers");
                if (decoded.Count >= maxEntries) throw new JsonException("sourceEventSeqs exceeds its event sequence");
                decoded.Add(seq);
                continue;
            }
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 2)
            {
                throw new JsonException("sourceEventSeqs range entries must be [start, end] pairs");
            }
            var items = entry.EnumerateArray().ToArray();
            var start = items[0].GetInt64();
            var end = items[1].GetInt64();
            if (start < 0 || end < start)
            {
                throw new JsonException("sourceEventSeqs ranges require 0 <= start <= end");
            }
            var length = end - start + 1;
            if (length > maxEntries - decoded.Count)
            {
                throw new JsonException("sourceEventSeqs range exceeds its event sequence");
            }
            for (var seq = start; seq <= end; seq += 1) decoded.Add(seq);
        }
        return decoded.ToArray();
    }

    /// <summary>Losslessly range-encode one strictly increasing source-sequence list.</summary>
    public static List<object?> EncodeSeqRanges(IReadOnlyList<long> values)
    {
        var encoded = new List<object?>();
        for (var start = 0; start < values.Count;)
        {
            var end = start;
            while (end + 1 < values.Count && values[end + 1] == values[end] + 1) end += 1;
            if (end - start >= 2)
            {
                encoded.Add(new long[] { values[start], values[end] });
            }
            else
            {
                for (var index = start; index <= end; index += 1) encoded.Add(values[index]);
            }
            start = end + 1;
        }
        return encoded;
    }

    private static string Truncate(string line) => line.Length <= 200 ? line : line[..200] + "…";
}