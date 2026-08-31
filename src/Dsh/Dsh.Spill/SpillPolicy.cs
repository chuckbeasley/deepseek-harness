using System.Text;

namespace Dsh.Spill;

/// <summary>
/// Result-retention policy for oversized plain-text tool results (port of packages/spill/spill-policy
/// minus the plugin/listener wiring): when a final result's UTF-8 size exceeds the inline cap, the
/// full text is saved to a session-scoped spill artifact and the model-facing text is replaced with
/// a bounded head/tail preview plus the backend's locator and retrieval guidance. Best-effort: a
/// storage failure or a missing within-cap replacement keeps the original inline.
/// </summary>
public static class SpillPolicy
{
    /// <summary>The retrieval guidance appended to every spill notice (the TS spill-policy hint).</summary>
    public const string RetrievalHint = "Use read with offset/limit, or grep this path to search within it.";

    /// <summary>UTF-8 byte length of one text.</summary>
    public static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// Build the bounded head/tail preview for <paramref name="text"/>, splitting
    /// <paramref name="budget"/> bytes across the two ends (head <c>ceil</c>, tail <c>floor</c>),
    /// each trimmed to a UTF-8 boundary at its cut. Reports the exact omitted byte count.
    /// </summary>
    public static (string Text, int OmittedBytes) Preview(string text, int budget)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var total = bytes.Length;
        var headBytes = (budget + 1) / 2;
        var tailBytes = budget / 2;
        var prefixLen = Math.Min(total, headBytes);
        var suffixLen = Math.Min(total - prefixLen, tailBytes);
        var keptPrefix = TrimTrailingPartialUtf8(bytes.AsSpan(0, prefixLen));
        var keptSuffix = TrimLeadingContinuationUtf8(bytes.AsSpan(bytes.Length - suffixLen));
        var kept = Encoding.UTF8.GetString(keptPrefix) + Encoding.UTF8.GetString(keptSuffix);
        return (kept, total - keptPrefix.Length - keptSuffix.Length);
    }

    /// <summary>The spill-notice line for a given omission and saved reference (no preview, no leading blank line).</summary>
    public static string Notice(int omittedBytes, string locator)
        => $"(Omitted {omittedBytes} bytes. Full formatted result stored at: {locator}. {RetrievalHint})";

    /// <summary>
    /// Spill <paramref name="text"/> and build the bounded replacement (preview + blank line +
    /// notice), or return <c>null</c> when the policy must keep the original: the text is within
    /// the cap, the save fails, or no within-cap replacement exists. The notice's byte cost is
    /// reserved INSIDE the cap (priced at the worst-case omission count), so the replacement never
    /// exceeds it.
    /// </summary>
    public static string? Replacement(
        string text,
        string sessionId,
        string suggestedName,
        ISpillService spill,
        int maxInlineBytes)
    {
        var total = ByteLength(text);
        if (total <= maxInlineBytes) return null;
        SpillFile file;
        try
        {
            file = spill.Claim(sessionId, suggestedName, text);
        }
        catch (Exception)
        {
            // Best-effort: a storage failure must never fail the call or hide the content.
            return null;
        }
        var reserve = ByteLength(Notice(total, file.Path)) + 2;
        var budget = Math.Max(0, maxInlineBytes - reserve);
        var (preview, omitted) = Preview(text, budget);
        var notice = Notice(omitted, file.Path);
        var replaced = preview.Length > 0 ? $"{preview}\n\n{notice}" : notice;
        // The policy NEVER emits a replacement larger than the cap (a tiny cap or a long spill
        // root leaves no within-cap replacement — keep the inline content).
        if (ByteLength(replaced) > maxInlineBytes) return null;
        return replaced;
    }

    /// <summary>Drop a trailing partial UTF-8 codepoint from the prefix cut (ASCII or a lead byte ends the kept run).</summary>
    private static byte[] TrimTrailingPartialUtf8(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length;
        while (length > 0)
        {
            var b = bytes[length - 1];
            if (b < 0x80) break; // ASCII: the cut is aligned.
            if (b >= 0xC0)
            {
                // A lead byte whose continuation bytes were all trimmed: drop it too.
                length--;
                break;
            }
            length--; // A continuation byte: keep stepping back.
        }
        return bytes[..length].ToArray();
    }

    /// <summary>Drop leading continuation bytes from the suffix cut so the kept tail starts at a codepoint boundary.</summary>
    private static byte[] TrimLeadingContinuationUtf8(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        while (start < bytes.Length && bytes[start] >= 0x80 && bytes[start] < 0xC0) start++;
        return bytes[start..].ToArray();
    }
}