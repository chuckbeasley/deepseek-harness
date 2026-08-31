namespace Dsh.Fs;

/// <summary>One applied-hunk diff card entry (port of the TS <c>FileDiff</c>).</summary>
public sealed record FsFileDiff(string Path, string? OldText, string NewText);

/// <summary>
/// Contextual-diff derivation for write/edit presentation metadata (port of tool-fs diff.ts over
/// jsdiff's <c>structuredPatch</c>). Tokens keep their line terminators, the Myers edit script
/// groups maximal runs, and hunks carry up to <see cref="Context"/> context lines on each side;
/// the emitted old/new texts are terminator-free line joins.
/// </summary>
public static class HunkDiffs
{
    /// <summary>Context lines shown on each side of an applied hunk (the TS DIFF_CONTEXT).</summary>
    public const int Context = 3;

    /// <summary>
    /// Compute one <see cref="FsFileDiff"/> per applied hunk between <paramref name="before"/> and
    /// <paramref name="after"/> (both LF-normalized), in file order; empty when the texts are
    /// identical. Pure insertions use <c>OldText: null</c>.
    /// </summary>
    public static IReadOnlyList<FsFileDiff> Compute(string path, string before, string after)
    {
        var parts = DiffParts(before, after);
        var hunks = BuildHunks(parts);
        var diffs = new List<FsFileDiff>(hunks.Count);
        foreach (var hunk in hunks)
        {
            var oldLines = new List<string>();
            var newLines = new List<string>();
            foreach (var line in hunk.Lines)
            {
                if (line.Length == 0) continue;
                var marker = line[0];
                // The unified-diff marker for a missing trailing newline annotates the patch, not
                // the content — skip it so it never leaks into a diff block.
                if (marker == '\\') continue;
                var text = line.Substring(1);
                if (marker == '-') oldLines.Add(text);
                else if (marker == '+') newLines.Add(text);
                else
                {
                    oldLines.Add(text);
                    newLines.Add(text);
                }
            }
            diffs.Add(new FsFileDiff(
                path,
                oldLines.Count > 0 ? string.Join('\n', oldLines) : null,
                string.Join('\n', newLines)));
        }
        return diffs;
    }

    private enum PartKind
    {
        Equal,
        Added,
        Removed,
    }

    private sealed record DiffPart(PartKind Kind, IReadOnlyList<string> Lines);

    private sealed record Hunk(IReadOnlyList<string> Lines);

    /// <summary>Tokenize like jsdiff lines: each token is a line with its terminator; a final unterminated line keeps no terminator.</summary>
    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\n') continue;
            tokens.Add(value.Substring(start, index - start + 1));
            start = index + 1;
        }
        if (start < value.Length)
        {
            tokens.Add(value.Substring(start));
        }
        return tokens;
    }

    /// <summary>The Myers edit script over tokens as maximal runs, in forward order.</summary>
    private static List<DiffPart> DiffParts(string before, string after)
    {
        var oldTokens = Tokenize(before);
        var newTokens = Tokenize(after);
        var script = Myers(oldTokens, newTokens);
        var parts = new List<DiffPart>();
        foreach (var op in script)
        {
            var kind = op.Kind switch
            {
                OpKind.Equal => PartKind.Equal,
                OpKind.Insert => PartKind.Added,
                _ => PartKind.Removed,
            };
            if (parts.Count > 0 && parts[^1].Kind == kind)
            {
                var merged = new List<string>(parts[^1].Lines) { op.Line };
                parts[^1] = new DiffPart(kind, merged);
            }
            else
            {
                parts.Add(new DiffPart(kind, new[] { op.Line }));
            }
        }
        return parts;
    }

    private enum OpKind
    {
        Equal,
        Insert,
        Delete,
    }

    private sealed record DiffOp(OpKind Kind, string Line);

    /// <summary>
    /// Classic O(ND) Myers diff over token arrays with the standard tie-break (prefer the
    /// insertion diagonal when both reach equally far), returning per-token ops in forward order.
    /// </summary>
    private static List<DiffOp> Myers(IReadOnlyList<string> oldTokens, IReadOnlyList<string> newTokens)
    {
        var n = oldTokens.Count;
        var m = newTokens.Count;
        var max = n + m;
        var result = new List<DiffOp>();
        if (max == 0) return result;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();
        var found = false;
        var d = 0;
        for (; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var index = k + max;
                int x;
                if (k == -d || (k != d && v[index - 1] < v[index + 1])) x = v[index + 1];
                else x = v[index - 1] + 1;
                var y = x - k;
                while (x < n && y < m && oldTokens[x] == newTokens[y])
                {
                    x++;
                    y++;
                }
                v[index] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
            if (found) break;
        }
        if (!found) return result;
        var tx = n;
        var ty = m;
        var prefix = int.MaxValue;
        for (var t = d; t > 0; t--)
        {
            var previous = trace[t];
            var k = tx - ty;
            var index = k + max;
            int previousK;
            if (k == -t || (k != t && previous[index - 1] < previous[index + 1])) previousK = k + 1;
            else previousK = k - 1;
            var previousX = previous[previousK + max];
            var previousY = previousX - previousK;
            while (tx > previousX && ty > previousY)
            {
                result.Add(new DiffOp(OpKind.Equal, oldTokens[tx - 1]));
                tx--;
                ty--;
            }
            if (tx == previousX)
            {
                result.Add(new DiffOp(OpKind.Insert, newTokens[ty - 1]));
                prefix = Math.Min(prefix, ty - 1);
            }
            else
            {
                result.Add(new DiffOp(OpKind.Delete, oldTokens[tx - 1]));
                prefix = Math.Min(prefix, tx - 1);
            }
            tx = previousX;
            ty = previousY;
        }
        result.Reverse();
        // The backtrack walks from the end, so the leading common run is position-implicit:
        // prepend it as explicit equal ops (jsdiff emits it as the opening equal part, which the
        // hunk builder uses as leading context).
        if (result.Count > 0 && result[0].Kind != OpKind.Equal && prefix < int.MaxValue)
        {
            var prefixOps = new List<DiffOp>(prefix);
            for (var i = 0; i < prefix; i++)
            {
                prefixOps.Add(new DiffOp(OpKind.Equal, oldTokens[i]));
            }
            result.InsertRange(0, prefixOps);
        }
        return result;
    }

    /// <summary>
    /// Assemble patch hunks from the diff parts (port of jsdiff's <c>diffLinesResultToPatch</c>
    /// with context 3): a change opens a hunk seeded with the previous part's trailing context
    /// lines; intervening context runs merge while at most <c>2 * Context</c> lines, otherwise the
    /// hunk closes with up to <c>Context</c> trailing lines. Hunk lines carry one marker prefix
    /// (' ', '+', '-') plus the raw token, with the trailing newline stripped afterwards.
    /// </summary>
    private static List<Hunk> BuildHunks(IReadOnlyList<DiffPart> parts)
    {
        // Append an empty equal part to make closing the final hunk uniform.
        var extended = new List<DiffPart>(parts) { new(PartKind.Equal, Array.Empty<string>()) };
        var hunks = new List<Hunk>();
        var rangeOpen = false;
        var oldLine = 1;
        var newLine = 1;
        List<string>? current = null;
        for (var index = 0; index < extended.Count; index++)
        {
            var part = extended[index];
            var lines = part.Lines;
            if (part.Kind != PartKind.Equal)
            {
                if (!rangeOpen)
                {
                    rangeOpen = true;
                    current = new List<string>();
                    if (index > 0)
                    {
                        var previous = extended[index - 1].Lines;
                        var take = Math.Min(Context, previous.Count);
                        for (var offset = previous.Count - take; offset < previous.Count; offset++)
                        {
                            current.Add(" " + previous[offset]);
                        }
                    }
                }
                var marker = part.Kind == PartKind.Added ? '+' : '-';
                foreach (var line in lines)
                {
                    current!.Add(marker + line);
                }
                if (part.Kind == PartKind.Added) newLine += lines.Count;
                else oldLine += lines.Count;
            }
            else
            {
                if (rangeOpen)
                {
                    if (lines.Count <= Context * 2 && index < extended.Count - 2)
                    {
                        foreach (var line in lines)
                        {
                            current!.Add(" " + line);
                        }
                    }
                    else
                    {
                        var contextSize = Math.Min(lines.Count, Context);
                        for (var offset = 0; offset < contextSize; offset++)
                        {
                            current!.Add(" " + lines[offset]);
                        }
                        hunks.Add(new Hunk(StripTerminators(current!)));
                        rangeOpen = false;
                        current = null;
                    }
                }
                oldLine += lines.Count;
                newLine += lines.Count;
            }
        }
        return hunks;
    }

    private static List<string> StripTerminators(List<string> lines)
    {
        var stripped = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            stripped.Add(line.EndsWith('\n') ? line.Substring(0, line.Length - 1) : line);
        }
        return stripped;
    }
}