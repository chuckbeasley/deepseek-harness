using System.Runtime.CompilerServices;
using System.Text;

namespace Dsh.Llm.DeepSeek;

/// <summary>
/// Decode an SSE byte stream into event <c>data</c> payloads. Framing is spec-strict: an event
/// dispatches only on its blank-line terminator, so an unterminated tail at EOF is truncation, not
/// a flushable payload. The literal <c>[DONE]</c> is yielded as the final payload and parsing
/// stops; EOF before it raises <c>STREAM_CLOSED</c>. Comments and non-data fields never enter the
/// payload stream.
/// </summary>
public static class SseParser
{
    /// <summary>The terminal payload DeepSeek sends after the last chunk.</summary>
    public const string Done = "[DONE]";

    /// <summary>
    /// Parse an SSE byte stream into data payloads in arrival order.
    /// </summary>
    /// <param name="stream">raw SSE bytes; reads may split anywhere, including mid-UTF-8 sequence.</param>
    /// <param name="ct">cancels the outstanding read and aborts parsing.</param>
    /// <returns>each event's data payload in arrival order; throws <c>STREAM_CLOSED</c> at EOF without <see cref="Done"/>.</returns>
#pragma warning disable CS8425 // the token is captured at call time; GetAsyncEnumerator supplies none
    public static async IAsyncEnumerable<string> ParseAsync(Stream stream, CancellationToken ct)
#pragma warning restore CS8425
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var data = new List<string>();
        var firstLine = true;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (firstLine)
            {
                firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            }
            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    var payload = string.Join("\n", data);
                    data.Clear();
                    yield return payload;
                    if (payload == Done) yield break;
                }
                continue;
            }
            if (line[0] == ':') continue; // comment lines carry no payload
            var colon = line.IndexOf(':');
            if (colon < 0) continue; // lines without a colon are ignored
            var value = colon + 1 < line.Length ? line[(colon + 1)..] : string.Empty;
            if (value.StartsWith(' ')) value = value[1..];
            if (line[..colon] == "data") data.Add(value);
            // event/id/retry fields are not part of the DeepSeek payload vocabulary.
        }
        throw new LlmError("SSE stream ended without [DONE]", "STREAM_CLOSED");
    }
}
