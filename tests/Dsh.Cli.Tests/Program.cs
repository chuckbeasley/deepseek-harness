namespace Dsh.Cli.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("args: bare invocation requires a profile", ArgsTests.BareInvocation_RequiresAProfile),
        ("args: bare -h prints the launcher help", ArgsTests.BareHelp_PrintsTheLauncherHelp),
        ("args: -V prints the version", ArgsTests.Version_PrintsAndExitsZero),
        ("args: inner arguments pass through", ArgsTests.ProfileBoot_PassesInnerArgumentsThrough),
        ("args: app -h passes through", ArgsTests.ProfileHelp_PassesThroughToTheApp),
        ("args: patches collect in order", ArgsTests.Patches_CollectInOrder),
        ("args: web alias", ArgsTests.WebAlias_FixesTheProfile),
        ("args: dump mutual exclusion", ArgsTests.DumpConfig_MutualExclusion),
        ("args: dumps take no app arguments", ArgsTests.DumpConfig_TakesNoAppArguments),
        ("args: default dump takes no patches", ArgsTests.DumpDefaultConfig_TakesNoPatches),
        ("args: plugin rejects parent options", ArgsTests.Plugin_RejectsParentOptions),
        ("args: plugin requires profile and args", ArgsTests.Plugin_RequiresProfileAndArgs),
        ("args: plugin resolves an invocation", ArgsTests.Plugin_ResolvesAnInvocation),
        ("boot: plugin initializes and manages bundles", BootTests.Plugin_InitializesAndManagesBundles),
        ("boot: dump-config composes layers boot-free", BootTests.DumpConfig_ComposesLayersBootFree),
        ("boot: headless runs one task through the real loop", BootTests.Headless_RunsOneTaskThroughTheRealLoop),
        ("boot: headless without a task exits 1", BootTests.Headless_WithoutATask_ExitsOne),
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
