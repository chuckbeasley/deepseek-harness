namespace Harness.Fs.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Harness> Factory, Action<Harness> Run)[] Suites = new (string, Func<Harness>, Action<Harness>)[]
    {
        ("text write/read round trip (create + update)", () => Harness.Create(), FileSystemServiceTests.TextWriteReadRoundTrip),
        ("binary bytes read back", () => Harness.Create(), FileSystemServiceTests.BinaryBytesRoundTrip),
        ("read text rejects binary files", () => Harness.Create(), FileSystemServiceTests.ReadTextRejectsBinaryFile),
        ("read text rejects invalid UTF-8", () => Harness.Create(), FileSystemServiceTests.ReadTextRejectsInvalidUtf8),
        ("read bytes rejects oversized content", () => Harness.Create(), FileSystemServiceTests.ReadBytesRejectsOversized),
        ("list/stat/delete/mkdir over the workspace", () => Harness.Create(), FileSystemServiceTests.ListStatDeleteMkdir),
        ("path traversal out of the root fails loud", () => Harness.Create(), FileSystemServiceTests.TraversalEscapesRootFailsLoud),
        ("missing files map to the typed error", () => Harness.Create(), FileSystemServiceTests.MissingFilesMapToTypedError),
        ("workspace root resolution honors an explicit spec", () => Harness.Create(), FileSystemServiceTests.WorkspaceRootResolutionHonorsExplicitSpec),
        ("write intents guard mutations", () => Harness.Create(), FileSystemServiceTests.WriteIntentsGuardMutations),
        ("write rejects a non-regular-file target", () => Harness.Create(), FileSystemServiceTests.WriteRejectsNonRegularFileTarget),
        ("read rejects a directory", () => Harness.Create(), FileSystemServiceTests.ReadRejectsDirectory),
        ("list rejects a non-directory", () => Harness.Create(), FileSystemServiceTests.ListRejectsNonDirectory),
        ("delete rejects a non-empty directory", () => Harness.Create(), FileSystemServiceTests.DeleteNonEmptyDirectoryFails),
        ("empty path is rejected", () => Harness.Create(), FileSystemServiceTests.EmptyPathRejected),
        ("version token changes on overwrite", () => Harness.Create(), FileSystemServiceTests.VersionChangesOnOverwrite),
        ("fs_write then fs_read through the runtime", () => Harness.Create(), FileSystemToolTests.WriteThenReadThroughRuntime),
        ("fs_read window and continuation footer", () => Harness.Create(), FileSystemToolTests.ReadWindowAndContinuationFooter),
        ("fs_read out-of-range offset fails", () => Harness.Create(), FileSystemToolTests.ReadOffsetOutOfRangeFails),
        ("fs_read missing file maps to the typed error", () => Harness.Create(), FileSystemToolTests.ReadMissingFileMapsToTypedError),
        ("fs_read directory fails through the tool", () => Harness.Create(), FileSystemToolTests.ReadDirectoryFailsThroughTool),
        ("invalid tool arguments are rejected", () => Harness.Create(), FileSystemToolTests.InvalidArgumentsAreRejected),
        ("fs_write overwrite renders Updated", () => Harness.Create(), FileSystemToolTests.WriteOverwriteRendersUpdated),
        ("empty content writes an empty file", () => Harness.Create(), FileSystemToolTests.EmptyContentWritesEmptyFile),
        ("fs_read truncates an over-long line", () => Harness.Create(), FileSystemToolTests.ReadTruncatesLongLine),
        ("fs_read byte cap truncates the window", () => Harness.Create(), FileSystemToolTests.ReadByteCapTruncatesWindow),
        ("hunk diffs match the jsdiff reference outputs", () => Harness.Create(), HunkDiffsTests.MatchesJsdiffReferenceOutputs),
        ("edit through the runtime with the observation policy", () => Harness.CreateWithPolicy(), FileSystemToolTests.EditThroughRuntimeWithPolicy),
        ("edit without a prior read refuses with the remedy", () => Harness.CreateWithPolicy(), FileSystemToolTests.EditWithoutReadRefusesWithRemedy),
        ("edit against a stale observed version refuses", () => Harness.CreateWithPolicy(), FileSystemToolTests.EditStaleVersionRefuses),
        ("ambiguous edit refuses then replace-all succeeds", () => Harness.CreateWithPolicy(), FileSystemToolTests.EditAmbiguousRefusesThenReplaceAllSucceeds),
        ("edit of a confirmed-absent target refuses with not-found", () => Harness.CreateWithPolicy(), FileSystemToolTests.EditConfirmedAbsentRefusesWithNotFound),
    };

    public static async Task<int> Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, factory, run) in Suites)
        {
            try
            {
                await using var harness = factory();
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
