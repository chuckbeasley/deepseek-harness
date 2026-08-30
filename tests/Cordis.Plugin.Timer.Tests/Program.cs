namespace Cordis.Plugin.Timer.Tests;

/// <summary>
/// Zero-dependency console assertion runner for the Phase 1 timer/logger port (mirrors
/// <c>tests\Cordis.Tests</c>). Each entry runs one scenario; failures are collected and the
/// process exits non-zero when any assertion fails.
/// </summary>
internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            Test("Timer.Apply_RegistersServiceUnderTimerKey_AndIsIdempotent", TimerServiceTests.Apply_RegistersServiceUnderTimerKey_AndIsIdempotent),
            Test("Timer.Timeout_FiresCallbackOnceOnSchedule", TimerServiceTests.Timeout_FiresCallbackOnceOnSchedule),
            Test("Timer.Timeout_DisposeCancelsPendingCallback", TimerServiceTests.Timeout_DisposeCancelsPendingCallback),
            Test("Timer.TimeoutAndInterval_ContextDispose_CancelsPendingCallbacks_NoPostDisposeInvocation", TimerServiceTests.TimeoutAndInterval_ContextDispose_CancelsPendingCallbacks_NoPostDisposeInvocation),
            Test("Timer.TimeoutAsync_ResolvesAfterDelay", TimerServiceTests.TimeoutAsync_ResolvesAfterDelay),
            Test("Timer.TimeoutAsync_FaultsWithContextDisposedOnTeardown", TimerServiceTests.TimeoutAsync_FaultsWithContextDisposedOnTeardown),
            Test("Timer.Interval_FiresRepeatedlyOnSchedule_UntilDisposed", TimerServiceTests.Interval_FiresRepeatedlyOnSchedule_UntilDisposed),
            Test("Timer.Interval_ContextDispose_CancelsPendingTicks", TimerServiceTests.Interval_ContextDispose_CancelsPendingTicks),
            Test("Timer.Interval_CallbackLongerThanPeriod_NeverOverlaps", TimerServiceTests.Interval_CallbackLongerThanPeriod_NeverOverlaps),
            Test("Timer.IntervalIterator_YieldsTicks_ThenFaultsOnContextDispose", TimerServiceTests.IntervalIterator_YieldsTicks_ThenFaultsOnContextDispose),
            Test("Timer.IntervalIterator_EnumeratorDisposal_EndsIterationGracefully", TimerServiceTests.IntervalIterator_EnumeratorDisposal_EndsIterationGracefully),
            Test("Timer.Throttle_ImmediateThenTrailing_DisposeCancelsPendingTrailing", TimerServiceTests.Throttle_ImmediateThenTrailing_DisposeCancelsPendingTrailing),
            Test("Timer.Debounce_FiresOnceAfterQuietPeriod_DisposeCancelsPending", TimerServiceTests.Debounce_FiresOnceAfterQuietPeriod_DisposeCancelsPending),
            Test("Timer.DelayAboveConfigCap_FailsLoud", TimerServiceTests.DelayAboveConfigCap_FailsLoud),
            Test("Timer.NegativeConfigMaxDelay_FailsLoudAtConstruction", TimerServiceTests.NegativeConfigMaxDelay_FailsLoudAtConstruction),
            Test("Timer.Timeout_OnDisposedContext_ThrowsInactiveEffect", TimerServiceTests.Timeout_OnDisposedContext_ThrowsInactiveEffect),
            Test("Logger.Exporter_RespectsLevelThreshold", ConsoleExporterTests.Exporter_RespectsLevelThreshold),
            Test("Logger.Exporter_WithDebugLevel_ReceivesEverything", ConsoleExporterTests.Exporter_WithDebugLevel_ReceivesEverything),
            Test("Logger.Exporter_RegistrationDisposer_RemovesExporter", ConsoleExporterTests.Exporter_RegistrationDisposer_RemovesExporter),
            Test("Logger.Exporter_ContextDispose_UnregistersExporter", ConsoleExporterTests.Exporter_ContextDispose_UnregistersExporter),
            Test("Logger.Exporter_FormatStability_PlainLine", ConsoleExporterTests.Exporter_FormatStability_PlainLine),
            Test("Logger.Exporter_FormatStability_WithTimestamp", ConsoleExporterTests.Exporter_FormatStability_WithTimestamp),
            Test("Logger.Exporter_MultiLineMessage_IndentsContinuation", ConsoleExporterTests.Exporter_MultiLineMessage_IndentsContinuation),
            Test("Logger.Exporter_MaxLength_TruncatesPerLine", ConsoleExporterTests.Exporter_MaxLength_TruncatesPerLine),
            Test("Logger.Exporter_ShowDiff_AppendsDuration", ConsoleExporterTests.Exporter_ShowDiff_AppendsDuration),
            Test("Logger.Exporter_Colors_EmitsAnsiOnlyWhenEnabled", ConsoleExporterTests.Exporter_Colors_EmitsAnsiOnlyWhenEnabled),
            Test("Logger.Exporter_Exception_RendersFullText", ConsoleExporterTests.Exporter_Exception_RendersFullText),
            Test("Logger.Exporter_RightAlignedLabel_MatchesVendoredLayout", ConsoleExporterTests.Exporter_RightAlignedLabel_MatchesVendoredLayout),
        };

        int passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL {name}: {error.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"== Cordis.Plugin.Timer.Tests: {passed} passed, {failures.Count} failed of {tests.Length} ==");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static (string Name, Func<Task> Run) Test(string name, Func<Task> run) => (name, run);
}
