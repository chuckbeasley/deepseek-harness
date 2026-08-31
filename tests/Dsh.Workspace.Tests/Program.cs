namespace Dsh.Workspace.Tests;

/// <summary>Zero-dependency console test runner for the workspace capability seam.</summary>
public static class Program
{
    private static readonly (string Name, Action<Harness> Run)[] Suites = new (string, Action<Harness>)[]
    {
        ("open validates an existing dir; lifecycle closes", WorkspaceTests.OpenValidatesExistingDirectoryAndLifecycle),
        ("open missing path fails loud", WorkspaceTests.OpenMissingPathFailsLoud),
        ("open a file path fails loud", WorkspaceTests.OpenFilePathFailsLoud),
        ("open an empty path fails loud", WorkspaceTests.OpenEmptyPathFailsLoud),
        ("second open of a different path fails loud", WorkspaceTests.SecondOpenDifferentPathFailsLoud),
        ("same canonical path returns the same workspace", WorkspaceTests.SameCanonicalPathReturnsSameWorkspace),
        ("status reflects directory presence", WorkspaceTests.StatusReflectsDirectoryPresence),
        ("title defaults to the directory name", WorkspaceTests.TitleDefaultsToDirectoryName),
        ("new workspace stamps a stable identity", WorkspaceTests.NewWorkspaceStampsStableIdentity),
    };

    private static readonly (string Name, Action Run)[] RegistrySuites = new (string, Action)[]
    {
        ("registry: create registers and resolves by path", WorkspaceRegistryTests.Create_RegistersAndResolvesByPath),
        ("registry: create rejects invalid paths", WorkspaceRegistryTests.Create_RejectsInvalidPaths),
        ("registry: a duplicate path rejects", WorkspaceRegistryTests.Create_DuplicatePath_Rejects),
        ("registry: rename updates the title and rejects blank and conflicts", WorkspaceRegistryTests.Rename_UpdatesTitle_AndRejectsBlankAndConflicts),
        ("registry: delete removes and updates the order", WorkspaceRegistryTests.Delete_RemovesAndUpdatesOrder),
        ("registry: insertBefore moves within the display order", WorkspaceRegistryTests.InsertBefore_MovesWithinTheDisplayOrder),
        ("registry: session membership attaches and moves", WorkspaceRegistryTests.SessionMembership_AttachesAndMoves),
        ("registry: archive requires a known session", WorkspaceRegistryTests.ArchiveSession_RequiresKnownSession),
        ("registry: persistence survives a restart", WorkspaceRegistryTests.Persistence_SurvivesRestart),
        ("registry: events are emitted after committed mutations", WorkspaceRegistryTests.Events_AreEmittedAfterCommittedMutations),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                using var harness = Harness.Create();
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
        foreach (var (name, run) in RegistrySuites)
        {
            try
            {
                run();
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
