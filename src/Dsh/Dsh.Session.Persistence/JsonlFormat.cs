using Dsh.Session;

namespace Dsh.Session.Persistence;

/// <summary>Thrown when a stored session log carries a format version this build cannot read.</summary>
public sealed class SessionFormatUnsupportedException : Exception
{
    public SessionFormatUnsupportedException(string message) : base(message)
    {
    }
}

/// <summary>
/// On-disk format helpers for the JSONL session-persistence backend: safe path encoding for
/// session ids, the per-session directory layout, and header-line (de)serialization. The first
/// JSONL record of a session artifact is a header envelope carrying the format version; every
/// later record is one serialized <see cref="SessionEvent"/> (its <c>$type</c> polymorphic
/// envelope round-trips through the same System.Text.Json options the session tests use).
/// </summary>
public static class JsonlFormat
{
    /// <summary>The JSON record tag distinguishing the first (header) line of a session log.</summary>
    public const string HeaderType = "session";

    /// <summary>The physical file name of one session's append-only log.</summary>
    public const string LogFileName = "session.jsonl";

    /// <summary>
    /// Encode an arbitrary string as a single safe path segment, injectively over all .NET
    /// strings. A session id is an unvalidated branded string, so this neutralizes separators,
    /// traversal, NUL, and drive escapes before any filesystem use. Safe code units remain
    /// literal; every other unit, including <c>~</c>, becomes <c>~XXXX</c>.
    /// </summary>
    /// <param name="raw">the string to encode; must be non-empty.</param>
    /// <returns>the escaped single path segment, decodable back to <paramref name="raw"/>.</returns>
    /// <exception cref="ArgumentException">when <paramref name="raw"/> is empty.</exception>
    public static string EncodeSegment(string raw)
    {
        if (raw.Length == 0) throw new ArgumentException("cannot encode an empty path segment", nameof(raw));
        if (raw == ".") return "~002E";
        if (raw == "..") return "~002E~002E";
        var output = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch != '~' && (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                output.Append(ch);
            }
            else
            {
                output.Append('~').Append(((int)ch).ToString("X4"));
            }
        }
        return output.ToString();
    }

    /// <summary>The append-only event-log file path for a session under a root.</summary>
    /// <param name="root">the backend's session root directory.</param>
    /// <param name="id">the session id, path-encoded via <see cref="EncodeSegment"/>.</param>
    /// <returns>the session's configured JSONL artifact path.</returns>
    public static string LogPath(string root, SessionId id)
    {
        return Path.Combine(root, EncodeSegment(id.Value), LogFileName);
    }

    /// <summary>Serialize the immutable session header as the first JSONL record (the format-version envelope).</summary>
    public static string HeaderLine(SessionHeader header)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", HeaderType);
            writer.WriteNumber("version", header.Version);
            writer.WriteString("id", header.Id.Value);
            writer.WriteNumber("createdAt", header.CreatedAtMs);
            writer.WriteString("cwd", header.Cwd);
            if (header.ParentSessionId is not null) writer.WriteString("parentSession", header.ParentSessionId);
            if (header.SeedLength is not null) writer.WriteNumber("seedLength", header.SeedLength.Value);
            writer.WriteNumber("delegationDepth", header.DelegationDepth);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Parse the first line of a log back into a <see cref="SessionHeader"/>. A header carrying a
    /// format version this build does not read is refused BEFORE any structural validation or event
    /// decoding: a future format need not satisfy this build's checks at all.
    /// </summary>
    /// <param name="line">the first line of a session artifact.</param>
    /// <param name="expectedId">the session id the caller requested; a mismatch is corruption.</param>
    /// <returns>the parsed header.</returns>
    /// <exception cref="JsonException">when the line is not a well-formed header or names a different session.</exception>
    /// <exception cref="SessionFormatUnsupportedException">when the header's version differs from <see cref="SessionFormat.Version"/>.</exception>
    public static SessionHeader ParseHeaderLine(string line, SessionId expectedId)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.GetString() != HeaderType
            || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("createdAt", out var createdAt) || createdAt.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException("corrupt session log: first line is not a session header");
        }
        var formatVersion = version.GetInt32();
        var parsedId = new SessionId(id.GetString()!);
        if (formatVersion != SessionFormat.Version)
        {
            throw new SessionFormatUnsupportedException(
                $"session log \"{parsedId}\" uses format version {formatVersion}, but this build reads format version {SessionFormat.Version}");
        }
        if (parsedId != expectedId)
        {
            throw new JsonException(
                $"corrupt session log: header id \"{parsedId}\" does not match requested id \"{expectedId}\"");
        }
        return new SessionHeader(
            formatVersion,
            parsedId,
            createdAt.GetInt64(),
            root.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String ? cwd.GetString()! : "",
            root.TryGetProperty("delegationDepth", out var depth) && depth.ValueKind == JsonValueKind.Number ? depth.GetInt32() : 0,
            root.TryGetProperty("parentSession", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null,
            root.TryGetProperty("seedLength", out var seed) && seed.ValueKind == JsonValueKind.Number ? seed.GetInt32() : null);
    }
}
