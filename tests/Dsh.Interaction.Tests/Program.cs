namespace Dsh.Interaction.Tests;

/// <summary>Zero-dependency console test runner for the interaction capability seam.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("approval: an ask outside an open turn rejects before appending", ApprovalServiceTests.AskOutsideAnOpenTurn_RejectsBeforeAppendingAnything),
        ("approval: the ask audits the pair and fails closed without an answerer", ApprovalServiceTests.AskAuditsThePairAndFailsClosedWithoutAnAnswerer),
        ("approval: an answerer on the waterfall decides the ask", ApprovalServiceTests.AnAnswererOnTheWaterfallDecidesTheAsk),
        ("approval: the never policy rejects deterministically before any dispatch", ApprovalServiceTests.NeverPolicy_RejectsDeterministically_BeforeAnyDispatch),
        ("approval: the session override beats the configured default", ApprovalServiceTests.SessionOverride_BeatsTheConfiguredDefault),
        ("approval: an aborted ask settles cancelled and discards the late answer", ApprovalServiceTests.AnAbortedAsk_SettlesCancelled_AndDiscardsTheLateAnswer),
        ("approval: a throwing answerer fails the ask closed", ApprovalServiceTests.AThrowingAnswerer_FailsTheAskClosed),
        ("questions: an empty question list is refused", UserQuestionTests.EmptyQuestions_Reject),
        ("questions: no answerer fails closed", UserQuestionTests.NoAnswerer_FailsClosed),
        ("questions: the answerer waterfall answers the ask", UserQuestionTests.TheAnswererWaterfall_AnswersTheAsk),
        ("questions: an aborted ask settles ask-aborted", UserQuestionTests.AnAbortedAsk_SettlesAskAborted),
        ("questions: the ask-user tool registers its definition", UserQuestionTests.AskUserTool_RegistersItsDefinition),
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
