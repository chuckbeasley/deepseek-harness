namespace Dsh.Skill.Tests;

/// <summary>Zero-dependency console runner for the Skill port tests.</summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>Run all tests; exit 0 only when every test passes.</summary>
    public static async Task<int> Main()
    {
        await RunAsync("discovery: lists directory-bundle and flat skills from a root", SkillTests.DiscoveryListsDirectoryAndFlatSkillsFromARoot);
        await RunAsync("loading: one skill's metadata and instructions resolve", SkillTests.LoadsOneSkillMetadataAndInstructions);
        await RunAsync("loading: missing skill and invalid names return null", SkillTests.MissingSkillAndInvalidNamesReturnNull);
        await RunAsync("provider: a missing skill root fails loud", SkillTests.MissingSkillRootFailsLoud);
        await RunAsync("tool: catalog tool executes through ToolRuntime", SkillTests.CatalogToolExecutesThroughToolRuntime);
        await RunAsync("registry: runtime registrations and duplicate-name rules", SkillTests.RuntimeRegistrationAndRegistryRules);
        await RunAsync("render: skill_content output is pinned and escaped", SkillTests.RenderSkillContentIsPinnedAndEscapes);
        await RunAsync("tool: registration fails loud without a tool registry", SkillTests.SkillToolRegistrationFailsLoudWithoutToolRegistry);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  PASS {name}");
            _passed++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"  FAIL {name}: {error.Message}");
            _failed++;
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"  PASS {name}");
            _passed++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"  FAIL {name}: {error.Message}");
            _failed++;
        }
    }
}
