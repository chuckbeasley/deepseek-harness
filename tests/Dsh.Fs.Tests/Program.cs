namespace Dsh.Fs.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action<Harness> Run)[] Suites = new (string, Action<Harness>)[]
    {
        ("text write/read round trip (create + update)", FileSystemServiceTests.TextWriteReadRoundTrip),
        ("binary bytes read back", FileSystemServiceTests.BinaryBytesRoundTrip),
        ("read text rejects binary files", FileSystemServiceTests.ReadTextRejectsBinaryFile),
        ("read text rejects invalid UTF-8", FileSystemServiceTests.ReadTextRejectsInvalidUtf8),
        ("read bytes rejects oversized content", FileSystemServiceTests.ReadBytesRejectsOversized),
        ("list/stat/delete/mkdir over the workspace", FileSystemServiceTests.ListStatDeleteMkdir),
        ("path traversal out of the root fails loud", FileSystemServiceTests.TraversalEscapesRootFailsLoud),
        ("missing files map to the typed error", FileSystemServiceTests.MissingFilesMapToTypedError),
        ("workspace root resolution honors an explicit spec", FileSystemServiceTests.WorkspaceRootResolutionHonorsExplicitSpec),
        ("write intents guard mutations", FileSystemServiceTests.WriteIntentsGuardMutations),
        ("write rejects a non-regular-file target", FileSystemServiceTests.WriteRejectsNonRegularFileTarget),
        ("read rejects a directory", FileSystemServiceTests.ReadRejectsDirectory),
        ("list rejects a non-directory", FileSystemServiceTests.ListRejectsNonDirectory),
        ("delete rejects a non-empty directory", FileSystemServiceTests.DeleteNonEmptyDirectoryFails),
        ("empty path is rejected", FileSystemServiceTests.EmptyPathRejected),
        ("version token changes on overwrite", FileSystemServiceTests.VersionChangesOnOverwrite),
        ("fs_write then fs_read through the runtime", FileSystemToolTests.WriteThenReadThroughRuntime),
        ("fs_read window and continuation footer", FileSystemToolTests.ReadWindowAndContinuationFooter),
        ("fs_read out-of-range offset fails", FileSystemToolTests.ReadOffsetOutOfRangeFails),
        ("fs_read missing file maps to the typed error", FileSystemToolTests.ReadMissingFileMapsToTypedError),
        ("fs_read directory fails through the tool", FileSystemToolTests.ReadDirectoryFailsThroughTool),
        ("invalid tool arguments are rejected", FileSystemToolTests.InvalidArgumentsAreRejected),
        ("fs_write overwrite renders Updated", FileSystemToolTests.WriteOverwriteRendersUpdated),
        ("empty content writes an empty file", FileSystemToolTests.EmptyContentWritesEmptyFile),
        ("fs_read truncates an over-long line", FileSystemToolTests.ReadTruncatesLongLine),
        ("fs_read byte cap truncates the window", FileSystemToolTests.ReadByteCapTruncatesWindow),
        ("hunk diffs match the jsdiff reference outputs", HunkDiffsTests.MatchesJsdiffReferenceOutputs),
    };

    public static async Task<int> Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                await using var harness = Harness.Create();
                run(harness);
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL  {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}
