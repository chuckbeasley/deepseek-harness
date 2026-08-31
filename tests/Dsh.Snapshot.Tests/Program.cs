namespace Dsh.Snapshot.Tests;

/// <summary>Zero-dependency console test runner for the recorded-session snapshot harness.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("replay: the fs-read fixture derives two recorded calls", ReplayScriptTests.TheFsReadFixture_DerivesTwoRecordedCalls),
        ("replay: packed rows expand into delta events", ReplayScriptTests.PackedRows_ExpandIntoDeltaEvents),
        ("replay: override docs validate loud", ReplayScriptTests.OverrideDocs_ValidateLoud),
        ("replay: the consumption check reports underruns", ReplayScriptTests.Install_ConsumptionCheck_ReportsUnderruns),
        ("e2e: the headless profile replays the recorded stream", EndToEndTests.TheHeadlessProfile_ReplaysTheRecordedStream),
    };

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "corpus")
        {
            return CorpusTests.RunCorpus(line => Console.WriteLine(line));
        }
        if (args.Length > 1 && args[0] == "diff")
        {
            CorpusTests.DiffScenario(args[1], line => Console.WriteLine(line));
            return 0;
        }
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                var watchdog = Task.Run(run);
                if (!watchdog.Wait(TimeSpan.FromSeconds(180)))
                {
                    throw new AssertionException("TIMEOUT (test hung)");
                }
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {error}");
                failures.Add(name);
            }
        }
        Console.WriteLine($"snapshot suites: {passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}