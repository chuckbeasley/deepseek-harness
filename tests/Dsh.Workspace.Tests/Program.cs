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
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}
