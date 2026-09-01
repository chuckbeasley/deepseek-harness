namespace Harness.SessionQuery.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("event-type filters select only matching events", SessionQueryTests.EventTypeFilters_SelectOnlyMatchingEvents),
        ("the message fold derives the surface", SessionQueryTests.MessageFold_DerivesTheSurface),
        ("turn enumeration folds open and closed turns", SessionQueryTests.TurnEnumeration_FoldsOpenAndClosedTurns),
        ("filters AND across clauses and match text flexibly", SessionQueryTests.Filters_AndWithinAClause_AndTextMatching),
        ("invalid filters fail loud", SessionQueryTests.InvalidFilters_FailLoud),
        ("the fold helper accumulates over the log", SessionQueryTests.FoldHelper_AccumulatesOverEvents),
        ("semantic text extraction covers the event vocabulary", SessionQueryTests.ExtractEventText_SemanticText),
        ("the provider registers as the sessionQuery service", SessionQueryTests.RegistersAsTheSessionQueryService),
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
