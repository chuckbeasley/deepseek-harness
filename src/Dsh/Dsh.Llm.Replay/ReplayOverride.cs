using System.Text.Json;
using Harness.Llm;

namespace Harness.Llm.Replay;

/// <summary>An override sidecar document: either a whole-script replacement or the augmentation form.</summary>
public sealed record OverrideDoc(IReadOnlyList<ReplayEntry>? WholeScript, IReadOnlyList<ReplayOverridePatch>? Patches);

/// <summary>Validation and parsing of <c>replay.override.json</c> sidecar documents.</summary>
public static class ReplayOverride
{
    private static readonly HashSet<string> ChunkTypes = new(StringComparer.Ordinal)
    {
        "block-start", "text-delta", "reasoning-delta", "tool-call-delta", "block-end", "usage", "finish",
    };

    /// <summary>Parse and validate one override document: a bare <c>ReplayEntry[]</c> or <c>{ patches }</c>.</summary>
    public static OverrideDoc ReadOverrideDoc(string text, string file)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var entries = new List<ReplayEntry>();
            var index = 0;
            foreach (var element in root.EnumerateArray())
            {
                entries.Add(ReadReplayEntry(element, file, $"entry {index}"));
                index++;
            }
            return new OverrideDoc(entries, null);
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(file, "document", "must be a ReplayEntry[] or { patches: [...] }");
        }
        if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(file, "document", "must be a ReplayEntry[] or { patches: [...] }");
        }
        var result = new List<ReplayOverridePatch>();
        var patchIndex = 0;
        foreach (var element in patches.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number
                || !element.TryGetProperty("entry", out var entry))
            {
                throw Invalid(file, $"patch {patchIndex}", "must contain exactly at and entry");
            }
            var atValue = at.GetInt32();
            if (atValue < 0)
            {
                throw Invalid(file, $"patch {patchIndex}", "at must be a non-negative safe integer");
            }
            result.Add(new ReplayOverridePatch(atValue, ReadReplayEntry(entry, file, $"patch {patchIndex}.entry")));
            patchIndex++;
        }
        return new OverrideDoc(null, result);
    }

    private static ReplayEntry ReadReplayEntry(JsonElement value, string file, string location)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out var kind))
        {
            throw Invalid(file, location, "must be an object");
        }
        switch (kind.GetString())
        {
            case "chunks":
            {
                var chunks = ReadChunks(value, file, location, "chunks");
                return new ChunksEntry(chunks);
            }
            case "throw":
            {
                var chunks = ReadChunks(value, file, location, "chunks");
                var message = RequiredString(value, "message", file, location);
                var code = RequiredString(value, "code", file, location);
                bool? accepted = value.TryGetProperty("accepted", out var acceptedValue) && acceptedValue.ValueKind == JsonValueKind.True
                    ? true
                    : value.TryGetProperty("accepted", out acceptedValue) && acceptedValue.ValueKind == JsonValueKind.False
                        ? false
                        : null;
                return new ThrowEntry(chunks, message, code, accepted);
            }
            case "hang":
            {
                string? readyFile = value.TryGetProperty("readyFile", out var ready) && ready.ValueKind == JsonValueKind.String
                    ? ready.GetString()
                    : null;
                return new HangEntry(readyFile);
            }
            default:
                throw Invalid(file, location, $"has unknown kind {JsonSerializer.Serialize(kind.GetString())}");
        }
    }

    private static List<StreamChunk> ReadChunks(JsonElement value, string file, string location, string name)
    {
        if (!value.TryGetProperty(name, out var chunks) || chunks.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(file, location, $"{name} must be an array");
        }
        var result = new List<StreamChunk>();
        var index = 0;
        foreach (var element in chunks.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !ChunkTypes.Contains(type.GetString()!))
            {
                throw Invalid(file, $"{location}.{name}[{index}]", "must have a known StreamChunk type");
            }
            result.Add(element.Deserialize<StreamChunk>(ReplayScript.FixtureJson)!);
            index++;
        }
        return result;
    }

    private static string RequiredString(JsonElement value, string name, string file, string location)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw Invalid(file, location, $"{name} must be a non-empty string");
        }
        var result = property.GetString() ?? "";
        if (result.Length == 0)
        {
            throw Invalid(file, location, $"{name} must be a non-empty string");
        }
        return result;
    }

    private static Exception Invalid(string file, string location, string detail)
        => new InvalidOperationException($"llm-replay: invalid override {file}: {location} {detail}");
}