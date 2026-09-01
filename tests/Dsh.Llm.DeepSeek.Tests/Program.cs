namespace Harness.Llm.DeepSeek.Tests;

/// <summary>
/// Zero-dependency console test runner for the DeepSeek LLM provider. The host sandbox blocks
/// dotnet build/dotnet test (MSBuild cannot spawn the C# compiler with captured output), so tests
/// run as a plain console app that exits non-zero on any assertion failure. All provider calls go
/// through the fake HttpMessageHandler — no network is ever touched.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Harness.Llm.DeepSeek - console assertions");
        Console.WriteLine();

        Run("Adapter: request shape pins URL, auth header, and body JSON", AdapterTests.RequestShape_PinsUrlAuthHeadersAndBody);
        Run("Adapter: optional fields omitted when absent", AdapterTests.OptionalFields_AreOmitted_WhenAbsent);
        Run("Adapter: thinking disabled from config", AdapterTests.ThinkingDisabled_FromConfig);
        Run("Adapter: thinking enabled with effort from config", AdapterTests.ThinkingEnabled_WithEffort_FromConfig);
        Run("Adapter: effort off disables thinking without effort field", AdapterTests.EffortOff_DisablesThinking_WithoutEffortField);
        Run("Adapter: disabled thinking with effort throws at construction", AdapterTests.ThinkingDisabledWithEffort_ThrowsAtConstruction);
        Run("Adapter: config API key wins over environment", AdapterTests.ConfigApiKey_WinsOverEnvironment);
        Run("Adapter: environment API key fallback", AdapterTests.EnvironmentApiKey_Fallback);
        Run("Adapter: missing API key maps to MISSING_CREDENTIAL", AdapterTests.MissingApiKey_ThrowsMissingCredential);
        Run("Adapter: config base URL wins over environment", AdapterTests.ConfigBaseUrl_WinsOverEnvironment);
        Run("Adapter: environment base URL fallback", AdapterTests.EnvironmentBaseUrl_Fallback);
        Run("Adapter: default base URL is the public API", AdapterTests.DefaultBaseUrl_IsPublicApi);
        Run("Adapter: 401 maps to AUTH with provider message", AdapterTests.Error401_MapsToAuth_WithProviderMessage);
        Run("Adapter: 429 maps to RATE_LIMIT", AdapterTests.Error429_MapsToRateLimit);
        Run("Adapter: 429 with quota wording maps to QUOTA", AdapterTests.Error429WithQuotaWording_MapsToQuota);
        Run("Adapter: 500 maps to SERVER", AdapterTests.Error500_MapsToServer);
        Run("Adapter: 400 context-window wording maps to CONTEXT_WINDOW_EXCEEDED", AdapterTests.Error400ContextWindow_MapsToContextWindowExceeded);
        Run("Adapter: generic 400 maps to INVALID_REQUEST", AdapterTests.Error400Generic_MapsToInvalidRequest);
        Run("Adapter: 413 maps to INVALID_REQUEST", AdapterTests.Error413_MapsToInvalidRequest);
        Run("Adapter: 418 maps to HTTP_418", AdapterTests.Error418_MapsToHttp418);
        Run("Adapter: malformed error body keeps the status message", AdapterTests.MalformedErrorBody_KeepsStatusMessage);
        Run("Adapter: cancelled before request throws OperationCanceled", AdapterTests.CancelledBeforeRequest_ThrowsOperationCanceled);
        Run("Adapter: cancelled mid-stream throws OperationCanceled", AdapterTests.CancelledMidStream_ThrowsOperationCanceled);
        Run("Adapter: transport failure maps to TRANSPORT", AdapterTests.TransportFailure_MapsToTransport);
        Run("Adapter: full stream assembles into a message", AdapterTests.FullStream_AssemblesIntoMessage);

        Run("SSE: multi-line data joins with newline", SseParserTests.MultiLineData_JoinsWithNewline);
        Run("SSE: comments and non-data fields are skipped", SseParserTests.CommentsAndNonDataFields_AreSkipped);
        Run("SSE: CRLF terminators are handled", SseParserTests.CrlfTerminators_AreHandled);
        Run("SSE: BOM is stripped", SseParserTests.BOM_IsStripped);
        Run("SSE: multiple events yield in order", SseParserTests.MultipleEvents_YieldInOrder);
        Run("SSE: [DONE] stops parsing", SseParserTests.Done_StopsParsing);
        Run("SSE: unterminated tail at EOF is truncation", SseParserTests.UnterminatedTail_AtEof_IsTruncation);
        Run("SSE: missing [DONE] throws STREAM_CLOSED", SseParserTests.MissingDone_ThrowsStreamClosed);
        Run("SSE: empty data field yields an empty payload", SseParserTests.EmptyDataField_YieldsEmptyPayload);
        Run("SSE: line without colon is ignored", SseParserTests.LineWithoutColon_IsIgnored);

        Run("Translate: text stream yields blocks and stop", TranslateTests.TextStream_YieldsBlocksAndStop);
        Run("Translate: reasoning then text open separate blocks", TranslateTests.ReasoningThenText_OpenSeparateBlocks);
        Run("Translate: empty initial reasoning delta does not open a block", TranslateTests.EmptyInitialReasoningDelta_DoesNotOpenBlock);
        Run("Translate: tool-call deltas concatenate into one block", TranslateTests.ToolCallDeltas_ConcatenateIntoOneBlock);
        Run("Translate: parallel tool calls open separate blocks", TranslateTests.ParallelToolCalls_OpenSeparateBlocks);
        Run("Translate: finish reason stop maps to Stop", TranslateTests.FinishReason_Stop);
        Run("Translate: finish reason tool_calls maps to ToolCalls", TranslateTests.FinishReason_ToolCalls);
        Run("Translate: finish reason length maps to MaxTokens", TranslateTests.FinishReason_Length);
        Run("Translate: unknown finish reason maps to Error", TranslateTests.UnknownFinishReason_MapsToError);
        Run("Translate: usage maps to disjoint counts", TranslateTests.Usage_MapsToDisjointCounts);
        Run("Translate: trailing usage-only chunk is emitted", TranslateTests.TrailingUsageOnlyChunk_IsEmitted);
        Run("Translate: empty response finish is an error", TranslateTests.EmptyResponse_FinishIsError);
        Run("Translate: malformed payload throws MALFORMED_RESPONSE", TranslateTests.MalformedPayload_ThrowsMalformedResponse);
        Run("Translate: missing [DONE] throws STREAM_CLOSED", TranslateTests.MissingDone_ThrowsStreamClosed);

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
    {
        try
        {
            test();
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
