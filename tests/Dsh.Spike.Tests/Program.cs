namespace Dsh.Spike.Tests;

/// <summary>
/// Zero-dependency console test runner for the Phase 0 spike. The host sandbox blocks
/// dotnet build/dotnet test (MSBuild cannot spawn the C# compiler with captured output), so
/// tests run as a plain console app that exits non-zero on any assertion failure.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Dsh Phase 0 spike - console assertions");
        Console.WriteLine();

        Run("Events: all event types round-trip through System.Text.Json", SessionEventTests.AllEventTypes_RoundTrip_ThroughSystemTextJson);
        Run("Events: envelope records seq and time", SessionEventTests.EventEnvelope_RecordsSeqAndTime);

        Run("Surface: user/message derives to its message", SurfaceTests.UserMessageEvent_DerivesToItsMessage);
        Run("Surface: empty assistant/message derives to null", SurfaceTests.EmptyAssistantMessage_DerivesToNull);
        Run("Surface: tool/result derives to its message", SurfaceTests.ToolResultEvent_DerivesToItsMessage);
        Run("Surface: non-surface event derives to null", SurfaceTests.NonSurfaceEvent_DerivesToNull);
        Run("Surface: eligibility matches the three message events", SurfaceTests.IsSurfaceEligibleType_MatchesTheThreeMessageEvents);

        Run("Assembler: tool-call stream assembles one tool-call block", BlockAssemblerTests.ToolCallStream_AssemblesOneToolCallBlock);
        Run("Assembler: text stream assembles from deltas without block-end", BlockAssemblerTests.TextStream_AssemblesFromDeltas_WithoutBlockEnd);
        Run("Assembler: interrupted blocks keep text prefix and drop tool calls", BlockAssemblerTests.InterruptedBlocks_KeepTextPrefix_AndDropToolCalls);
        Run("Assembler: max-tokens finish drops tool calls", BlockAssemblerTests.MaxTokensFinish_DropsToolCalls);
        Run("Assembler: usage chunk is retained", BlockAssemblerTests.UsageChunk_IsRetained);
        Run("Assembler: straggler delta after block-end is ignored", BlockAssemblerTests.StragglerDelta_AfterBlockEnd_IsIgnored);

        Run("Mock: first call streams one todo_write tool call", MockLlmProviderTests.FirstCall_StreamsOneTodoWriteToolCall);
        Run("Mock: second call streams plain text and stops", MockLlmProviderTests.SecondCall_StreamsPlainTextAndStops);
        Run("Mock: cancelled token aborts before any chunk", MockLlmProviderTests.CancelledToken_AbortsBeforeAnyChunk);

        Run("Todo: write replaces whole list and computes counts", TodoToolTests.Write_ReplacesWholeList_AndComputesCounts);
        Run("Todo: write rejects empty content", TodoToolTests.Write_RejectsEmptyContent);
        Run("Todo: write rejects duplicate content", TodoToolTests.Write_RejectsDuplicateContent);
        Run("Todo: write rejects two in_progress when parallel disabled", TodoToolTests.Write_RejectsTwoInProgress_WhenParallelDisabled);
        Run("Todo: write allows two in_progress when parallel enabled", TodoToolTests.Write_AllowsTwoInProgress_WhenParallelEnabled);
        Run("Todo: description matches the pinned fixture literal", TodoToolTests.Describe_MatchesThePinnedFixtureLiteral);
        Run("Todo: definition execute returns canonical result and render projects text", TodoToolTests.Definition_Execute_ReturnsCanonicalResult_AndRender_ProjectsText);
        Run("Todo: todo/write event round-trips as declared type", TodoToolTests.TodoWriteEvent_RoundTripsAsDeclaredType);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (_failed > 0)
        {
            foreach (var failure in Failures)
            {
                Console.WriteLine("  FAILED: " + failure);
            }
            return 1;
        }
        return 0;
    }

    private static void Run(string name, Action test)
        => Run(name, () =>
        {
            test();
            return Task.CompletedTask;
        });

    private static void Run(string name, Func<Task> test)
    {
        try
        {
            test().GetAwaiter().GetResult();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (AssertionException ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }
}
