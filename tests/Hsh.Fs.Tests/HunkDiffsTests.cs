namespace Harness.Fs.Tests;

/// <summary>
/// Parity tests for <see cref="HunkDiffs"/> against jsdiff 9.0.0 <c>structuredPatch</c> with
/// context 3 (the reference outputs were generated with the real package and are pinned here).
/// </summary>
public static class HunkDiffsTests
{
    public static void MatchesJsdiffReferenceOutputs(Harness harness)
    {
        var cases = new (string Before, string After, FsFileDiff[] Expected)[]
        {
            ("original contents\n", "replaced", new[]
            {
                new FsFileDiff("data.txt", "original contents", "replaced"),
            }),
            ("a\nb\nc\nOLD\nd\ne\nf\n", "a\nb\nc\nNEW\nd\ne\nf\n", new[]
            {
                new FsFileDiff("a.txt", "a\nb\nc\nOLD\nd\ne\nf", "a\nb\nc\nNEW\nd\ne\nf"),
            }),
            ("line1\nline2\nline3\nline4\nline5\nline6\nline7\n", "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\n", new[]
            {
                new FsFileDiff("f.txt", "line5\nline6\nline7", "line5\nline6\nline7\nline8"),
            }),
            ("", "brand new", new[]
            {
                new FsFileDiff("f.txt", null, "brand new"),
            }),
            ("gone\n", "", new[]
            {
                new FsFileDiff("f.txt", "gone", ""),
            }),
            ("x\n", "y\n", new[]
            {
                new FsFileDiff("f.txt", "x", "y"),
            }),
            ("same\n", "same\n", Array.Empty<FsFileDiff>()),
            ("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n", "a\nb\nc\nd\nX\ne\nf\ng\nh\ni\nj\n", new[]
            {
                new FsFileDiff("f.txt", "b\nc\nd\ne\nf\ng", "b\nc\nd\nX\ne\nf\ng"),
            }),
            ("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\n", "a\nb\nc\nd\nX\ne\nf\ng\nh\ni\nj\nk\n", new[]
            {
                new FsFileDiff("f.txt", "b\nc\nd\ne\nf\ng", "b\nc\nd\nX\ne\nf\ng"),
            }),
            ("mode=DEBUG\nlevel=info\n", "mode=RELEASE\nlevel=info\n", new[]
            {
                new FsFileDiff("config.txt", "mode=DEBUG\nlevel=info", "mode=RELEASE\nlevel=info"),
            }),
        };
        foreach (var (before, after, expected) in cases)
        {
            var actual = HunkDiffs.Compute(expected.Length > 0 ? expected[0].Path : "f.txt", before, after);
            Assert.Equal(expected.Length, actual.Count, $"hunk count for {Json(before)} -> {Json(after)}");
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index].OldText, actual[index].OldText, $"oldText[{index}] for {Json(before)} -> {Json(after)}");
                Assert.Equal(expected[index].NewText, actual[index].NewText, $"newText[{index}] for {Json(before)} -> {Json(after)}");
            }
        }
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}