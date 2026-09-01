namespace Harness.Identity.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("first use creates and persists the anonymous id", IdentityTests.FirstUse_CreatesAndPersistsId),
        ("a second provider instance reads the same persisted id", IdentityTests.SecondProviderInstance_ReadsTheSameId),
        ("a corrupt id file fails loud", IdentityTests.CorruptIdFile_FailsLoud),
        ("$HSH_HOME is respected via HomePaths", IdentityTests.HshHomeEnv_IsRespected),
        ("deleting the id file mints a fresh identity", IdentityTests.DeletedFile_MintsAFreshIdentity),
        ("the provider registers as the identity service", IdentityTests.RegistersAsTheIdentityService),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
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
