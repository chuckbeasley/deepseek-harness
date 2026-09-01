namespace Harness.Feedback.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites =
    {
        ("an empty log yields an empty feedback state", FeedbackTests.EmptyLog_YieldsAnEmptyState),
        ("feedback/write events fold last-write-wins per message in creation order", FeedbackTests.FeedbackEvents_FoldLastWriteWinsPerMessageInCreationOrder),
        ("the state updates live on session/event", FeedbackTests.StateUpdatesLiveOnSessionEvent),
        ("the feedback event round-trips the JSONL (put and delete)", FeedbackTests.FeedbackEvent_RoundTripsTheJsonl),
        ("message_feedback executes through the tool runtime and appends the durable event", FeedbackTests.MessageFeedbackTool_ExecutesThroughToolRuntime_AndAppendsTheDurableEvent),
        ("put rejects blank and oversized notes", FeedbackTests.Put_RejectsBlankAndOversizedNotes),
        ("delete removes an item and absence is idempotent", FeedbackTests.Delete_RemovesAnItem_AndAbsenceIsIdempotent),
        ("the service registers as the feedback service", FeedbackTests.RegistersAsTheFeedbackService),
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
