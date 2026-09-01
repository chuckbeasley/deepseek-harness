using System.Text;

namespace Harness.Cordis.Cosmokit;

/// <summary>
/// How much content a retainer omitted: <c>None</c>, an <c>Exact</c> count
/// (every unit/byte was observed), or <c>Unknown</c> (a caller omitted without
/// a count; the retainers themselves never return it).
/// </summary>
public abstract record Omitted
{
    /// <summary>Nothing was omitted.</summary>
    public sealed record None() : Omitted;

    /// <summary>A precise count of omitted units/bytes.</summary>
    public sealed record Exact(int Count) : Omitted;

    /// <summary>Omission happened but the count is unknown.</summary>
    public sealed record Unknown() : Omitted;
}

/// <summary>The caller receives this after each <c>Push</c>.</summary>
/// <param name="Kept">Whether this whole unit / all of this chunk's bytes were retained.</param>
/// <param name="Truncated">Cumulative: whether the retainer has omitted anything due to the budget yet.</param>
public readonly record struct PushDecision(bool Kept, bool Truncated);

/// <summary>Final result for ordered logical units.</summary>
/// <param name="Items">The retained units.</param>
/// <param name="Truncated">Whether anything was omitted due to the budget.</param>
/// <param name="Seen">Units observed by the retainer.</param>
/// <param name="Kept">The number of retained units, surfaced for notice formatters.</param>
/// <param name="Omitted">The omission metadata.</param>
public sealed record RetainedItems<T>(IReadOnlyList<T> Items, bool Truncated, int Seen, int Kept, Omitted Omitted);

/// <summary>Final result for text streams.</summary>
/// <param name="Text">The retained text, safe to hand to a formatter.</param>
/// <param name="Truncated">Whether anything was omitted due to the budget.</param>
/// <param name="OmittedBytes">Byte-oriented omission metadata.</param>
public sealed record RetainedText(string Text, bool Truncated, Omitted OmittedBytes);

/// <summary>Item retention strategy. Only <c>Head</c> in v1.</summary>
/// <param name="MaxItems">Keep the first <paramref name="MaxItems"/> units.</param>
public sealed record ItemRetentionStrategy(int MaxItems);

/// <summary>Text retention strategy: keep a prefix, a suffix, or both, counted in bytes.</summary>
public abstract record TextRetentionStrategy
{
    /// <summary>Keep the first <paramref name="MaxBytes"/> bytes.</summary>
    public sealed record Head(int MaxBytes) : TextRetentionStrategy;

    /// <summary>Keep the final <paramref name="MaxBytes"/> bytes. Requires reading to the end.</summary>
    public sealed record Tail(int MaxBytes) : TextRetentionStrategy;

    /// <summary>Keep a stable prefix and suffix, omitting the middle. Requires reading to the end.</summary>
    public sealed record HeadTail(int HeadBytes, int TailBytes) : TextRetentionStrategy;
}

/// <summary>A neutral, tool-agnostic description of one retention outcome — the input to a notice formatter.</summary>
/// <param name="Scope">Tool/scope label, e.g. <c>grep</c>.</param>
/// <param name="Strategy"><c>head</c>, <c>tail</c>, or <c>headTail</c>.</param>
/// <param name="Unit"><c>items</c>, <c>bytes</c>, <c>chars</c>, or <c>lines</c>.</param>
/// <param name="Limit">The budget: head-only or head/tail byte counts.</param>
/// <param name="Kept">How much was kept.</param>
/// <param name="Omitted">The omission metadata.</param>
public sealed record RetentionNotice(string Scope, string Strategy, string Unit, RetentionLimit Limit, int Kept, Omitted Omitted);

/// <summary>The budget of a <see cref="RetentionNotice"/>: a head count, or head and tail counts.</summary>
/// <param name="Head">The head budget, or <c>null</c> for tail-only.</param>
/// <param name="Tail">The tail budget, or <c>null</c> for head-only.</param>
public sealed record RetentionLimit(int? Head, int? Tail)
{
    /// <summary>A head-only budget.</summary>
    public static RetentionLimit HeadOnly(int head) => new(head, null);

    /// <summary>A head/tail budget.</summary>
    public static RetentionLimit HeadTail(int head, int tail) => new(head, tail);
}

/// <summary>
/// Bounds an ordered stream of logical units to the head budget of an
/// <see cref="TextRetentionStrategy.Head"/> strategy. <c>Push</c> reports, per
/// unit, whether it was kept and whether the retained result is now truncated.
/// The caller pushes every observed unit, so the final <see cref="Omitted"/>
/// count is exact.
/// </summary>
public sealed class ItemRetainer<T>
{
    private readonly int _maxItems;
    private readonly List<T> _items = new();
    private int _seen;
    private int _omittedCount;

    /// <summary>Creates a retainer with the given head strategy.</summary>
    /// <exception cref="ArgumentException"><paramref name="strategy"/> is not a non-negative integer budget.</exception>
    public ItemRetainer(ItemRetentionStrategy strategy)
    {
        AssertBudget(strategy.MaxItems, "maxItems");
        _maxItems = strategy.MaxItems;
    }

    /// <summary>Offers one unit; kept while below the cap, otherwise dropped and counted as omitted.</summary>
    public PushDecision Push(T item)
    {
        _seen++;
        if (_items.Count < _maxItems)
        {
            _items.Add(item);
            return new PushDecision(true, false);
        }
        _omittedCount++;
        return new PushDecision(false, true);
    }

    /// <summary>Finalizes and reports what was kept and omitted.</summary>
    public RetainedItems<T> Finish()
    {
        var truncated = _omittedCount > 0;
        return new RetainedItems<T>(
            _items,
            truncated,
            _seen,
            _items.Count,
            truncated ? new Omitted.Exact(_omittedCount) : new Omitted.None());
    }

    private static void AssertBudget(int value, string name)
    {
        if (value < 0) throw new ArgumentException($"{name} must be a non-negative integer", name);
    }
}

/// <summary>
/// Bounds a byte-oriented text stream, keeping a prefix, a suffix, or both
/// (all <see cref="TextRetentionStrategy"/> variants). Caps and omitted counts
/// are byte counts. <c>Finish</c> trims a partial codepoint at each cut, so the
/// returned text never introduces a replacement char at the boundary, and the
/// retainer holds at most <c>prefixCap + tailBytes + one chunk</c> in memory.
/// </summary>
public sealed class TextRetainer
{
    private readonly int _prefixCap;
    private readonly int _suffixCap;
    private readonly List<byte[]> _prefixChunks = new();
    private int _prefixHeld;
    private readonly List<byte[]> _suffixChunks = new();
    private int _suffixHeld;
    private int _total;

    /// <summary>Creates a retainer for the given strategy; byte budgets must be non-negative integers.</summary>
    /// <exception cref="ArgumentException">A budget is not a non-negative integer.</exception>
    public TextRetainer(TextRetentionStrategy strategy)
    {
        switch (strategy)
        {
            case TextRetentionStrategy.Head head:
                AssertBudget(head.MaxBytes, "maxBytes");
                _prefixCap = head.MaxBytes;
                _suffixCap = 0;
                break;
            case TextRetentionStrategy.Tail tail:
                AssertBudget(tail.MaxBytes, "maxBytes");
                _prefixCap = 0;
                _suffixCap = tail.MaxBytes;
                break;
            case TextRetentionStrategy.HeadTail headTail:
                AssertBudget(headTail.HeadBytes, "headBytes");
                AssertBudget(headTail.TailBytes, "tailBytes");
                _prefixCap = headTail.HeadBytes;
                _suffixCap = headTail.TailBytes;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy));
        }
    }

    /// <summary>Offers one chunk of bytes. <see cref="PushDecision.Kept"/> is true only when no byte was dropped.</summary>
    public PushDecision Push(byte[] chunk)
    {
        var before = _total;
        _total += chunk.Length;

        var room = _prefixCap - _prefixHeld;
        var take = Math.Max(0, Math.Min(room, chunk.Length));
        if (take > 0)
        {
            _prefixChunks.Add(chunk.AsSpan(0, take).ToArray());
            _prefixHeld += take;
        }

        if (_suffixCap > 0)
        {
            _suffixChunks.Add(chunk);
            _suffixHeld += chunk.Length;
            while (_suffixChunks.Count > 0 && _suffixHeld - _suffixChunks[0].Length >= _suffixCap)
            {
                _suffixHeld -= _suffixChunks[0].Length;
                _suffixChunks.RemoveAt(0);
            }
            if (_suffixChunks.Count > 0 && _suffixHeld > _suffixCap)
            {
                var excess = _suffixHeld - _suffixCap;
                _suffixChunks[0] = _suffixChunks[0].AsSpan(excess).ToArray();
                _suffixHeld -= excess;
            }
        }

        var droppedThisChunk = OmittedAt(_total) > OmittedAt(before);
        return new PushDecision(!droppedThisChunk, OmittedAt(_total) > 0);
    }

    /// <summary>Offers one chunk encoded as UTF-8.</summary>
    public PushDecision Push(string chunk) => Push(Encoding.UTF8.GetBytes(chunk));

    /// <summary>
    /// Finalizes: decodes the retained prefix and suffix (each trimmed to a
    /// UTF-8 boundary at its cut) and reports the exact omitted byte count.
    /// </summary>
    public RetainedText Finish()
    {
        var prefixLen = Math.Min(_total, _prefixCap);
        var suffixLen = Math.Min(_total - prefixLen, _suffixCap);

        var prefix = Concat(_prefixChunks);
        var suffix = Concat(_suffixChunks).AsSpan(_suffixHeld - suffixLen).ToArray();

        var budgetOmitted = OmittedAt(_total);
        byte[] keptPrefix;
        byte[] keptSuffix;
        string text;
        if (budgetOmitted > 0)
        {
            keptPrefix = TrimTrailingPartialUtf8(prefix);
            keptSuffix = TrimLeadingContinuationUtf8(suffix);
            text = Encoding.UTF8.GetString(keptPrefix) + Encoding.UTF8.GetString(keptSuffix);
        }
        else
        {
            keptPrefix = prefix;
            keptSuffix = suffix;
            text = Encoding.UTF8.GetString(Concat([prefix, suffix]));
        }

        var omitted = _total - keptPrefix.Length - keptSuffix.Length;
        var truncated = omitted > 0;
        return new RetainedText(
            text,
            truncated,
            truncated ? new Omitted.Exact(omitted) : new Omitted.None());
    }

    private static void AssertBudget(int value, string name)
    {
        if (value < 0) throw new ArgumentException($"{name} must be a non-negative integer", name);
    }

    /// <summary>Bytes omitted once <paramref name="total"/> bytes have been seen.</summary>
    private int OmittedAt(int total)
    {
        var prefixLen = Math.Min(total, _prefixCap);
        var suffixLen = Math.Min(total - prefixLen, _suffixCap);
        return total - prefixLen - suffixLen;
    }

    private static byte[] Concat(IReadOnlyList<byte[]> chunks)
    {
        var length = 0;
        foreach (var chunk in chunks) length += chunk.Length;
        var output = new byte[length];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(output, offset);
            offset += chunk.Length;
        }
        return output;
    }

    private static byte[] TrimTrailingPartialUtf8(byte[] bytes)
    {
        var i = bytes.Length - 1;
        while (i >= 0 && (bytes[i] & 0xc0) == 0x80 && bytes.Length - i <= 3) i--;
        if (i < 0) return bytes;
        var lead = bytes[i];
        var expected = lead < 0x80 ? 1 : lead < 0xe0 ? 2 : lead < 0xf0 ? 3 : lead < 0xf8 ? 4 : 0;
        if (expected == 0) return bytes;
        return bytes.Length - i < expected ? bytes.AsSpan(0, i).ToArray() : bytes;
    }

    private static byte[] TrimLeadingContinuationUtf8(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length && (bytes[i] & 0xc0) == 0x80) i++;
        return i == 0 ? bytes : bytes.AsSpan(i).ToArray();
    }
}

/// <summary>Standardized, false-precision-safe wording for retention outcomes.</summary>
public static class Retention
{
    /// <summary>Formats one <see cref="Omitted"/> value into a clause.</summary>
    /// <param name="omitted">The omission metadata from a retainer result.</param>
    /// <param name="unit">The noun for the omitted quantity.</param>
    /// <returns>A neutral clause (no trailing space), or an empty string when nothing was omitted.</returns>
    public static string DescribeOmitted(Omitted omitted, string unit)
    {
        return omitted switch
        {
            Omitted.None => string.Empty,
            Omitted.Exact exact => $"Omitted {exact.Count} {unit}.",
            Omitted.Unknown => $"More {unit} were omitted.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Turns a <see cref="RetentionNotice"/> into a one-line footer: the
    /// standardized omission clause followed by tool-supplied recovery
    /// guidance. Either half may be empty; the two are joined with a single
    /// space.
    /// </summary>
    /// <param name="notice">The neutral retention outcome.</param>
    /// <param name="recovery">Tool-supplied guidance builder; receives the notice, returns a sentence (or an empty string).</param>
    /// <returns>The combined footer line.</returns>
    public static string FormatRetentionNotice(RetentionNotice notice, Func<RetentionNotice, string> recovery)
    {
        return string.Join(' ', new[] { DescribeOmitted(notice.Omitted, notice.Unit), recovery(notice) }
            .Where(part => part.Length > 0));
    }
}




