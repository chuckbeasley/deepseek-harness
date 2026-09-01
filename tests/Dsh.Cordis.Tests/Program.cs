using Harness.Cordis.Core;
using Harness.CordisRunner;
using Harness.Llm;
using Harness.Session;
using Harness.Tools;

namespace Harness.CordisRunner.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                run().GetAwaiter().GetResult();
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

    private static readonly (string Name, Func<Task> Run)[] Suites =
    {
        ("cordis_define mints and renders the receipt", CordisToolsTests.Define_MintsAndRendersTheReceipt),
        ("cordis_run reports the host-only activation", CordisToolsTests.Run_ReportsTheHostOnlyActivation),
        ("cordis_inspect_self plugin mode matches the recorded value", CordisToolsTests.InspectSelf_PluginModeMatchesTheRecordedValue),
        ("cordis_undefine removes the plugin", CordisToolsTests.Undefine_RemovesThePlugin),
        ("cordis_inspect_query tools contract matches the recorded fixture", CordisToolsTests.InspectQuery_ToolsContractMatchesTheRecordedFixture),
        ("cordis_inspect_query service directory mode", CordisToolsTests.InspectQuery_ServiceDirectoryMode),
        ("cordis_inspect_list lists the four host providers", CordisToolsTests.InspectList_ListsTheFourHostProviders),
        ("cordis_inspect_query unknown provider and method fail loud", CordisToolsTests.InspectQuery_UnknownProviderAndMethodFailLoud),
        ("cordis_inspect_query builtin and tool providers", CordisToolsTests.InspectQuery_BuiltinAndToolProviders),
        ("run_code integration reproduces the recorded step", CordisToolsTests.RunCode_IntegrationReproducesTheRecordedStep),
    };
}