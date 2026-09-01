namespace Dsh.Lsp.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        // Probe: the recorded corpus path through the embedded fixture server (no node).
        ("probe: fixture provider reproduces the recorded render", NodeServerProbe.FixtureProvider_ReproducesTheRecordedRender),
        // Transport over in-memory duplexes (existing).
        ("transport round-trips a framed message", LspTests.Transport_RoundTripsAFramedMessage),
        ("client request gets its response", LspTests.Client_RequestGetsItsResponse),
        ("client dispatches notifications", LspTests.Client_DispatchesNotifications),
        ("missing Content-Length fails loud", LspTests.Transport_MissingLengthHeaderFailsLoud),
        // Framing (§14.2).
        ("framing encodes a Content-Length header with the utf-8 byte length", FramingTests.Encode_PrefixesContentLengthHeaderWithUtf8ByteLength),
        ("decoder reads a single framed message", FramingTests.Decode_SingleFramedMessage),
        ("decoder reads multiple messages in one chunk", FramingTests.Decode_MultipleMessagesInOneChunk),
        ("decoder reassembles a message split across chunks", FramingTests.Decode_ReassemblesMessageSplitAcrossChunks),
        ("decoder handles a header split from its body", FramingTests.Decode_HandlesHeaderSplitFromBody),
        ("decoder reads case-insensitive headers and ignores others", FramingTests.Decode_ReadsCaseInsensitiveHeaderAndIgnoresOtherHeaders),
        ("decoder rejects a body over the size limit", FramingTests.Decode_RejectsBodyOverSizeLimit),
        ("decoder rejects a missing Content-Length header", FramingTests.Decode_RejectsMissingContentLength),
        ("decoder rejects a non-numeric Content-Length header", FramingTests.Decode_RejectsNonNumericContentLength),
        ("decoder rejects an unterminated header block", FramingTests.Decode_RejectsUnterminatedHeaderBlock),
        ("decoder rejects an oversized header block", FramingTests.Decode_RejectsOversizedHeaderWithTerminator),
        ("decoder rejects a non-JSON body", FramingTests.Decode_RejectsNonJsonBody),
        // Translate (§14.3).
        ("requestMethod maps each operation", TranslateTests.RequestMethod_MapsEachOperation),
        ("supportsOperation reads provider slots", TranslateTests.SupportsOperation_ReadsProviderSlotBooleanAndOptionsForms),
        ("supportsTransientOpen accepts legacy enums", TranslateTests.SupportsTransientOpen_LegacyEnums),
        ("supportsTransientOpen reads options forms", TranslateTests.SupportsTransientOpen_OptionsForms),
        ("supportsTransientOpen requires explicit openClose", TranslateTests.SupportsTransientOpen_RequiresExplicitOpenClose),
        ("negotiatePositionEncoding defaults omitted to utf-16", TranslateTests.NegotiatePositionEncoding_DefaultsOmittedToUtf16),
        ("negotiatePositionEncoding rejects other encodings", TranslateTests.NegotiatePositionEncoding_RejectsOtherEncodings),
        ("normalizeLocations handles null and missing", TranslateTests.NormalizeLocations_NullAndMissing),
        ("normalizeLocations maps a single Location", TranslateTests.NormalizeLocations_MapsASingleLocation),
        ("normalizeLocations maps an array", TranslateTests.NormalizeLocations_MapsAnArray),
        ("normalizeLocations maps LocationLinks", TranslateTests.NormalizeLocations_MapsLocationLinks),
        ("normalizeLocations rejects a non-object entry", TranslateTests.NormalizeLocations_RejectsNonObjectEntry),
        ("normalizeLocations rejects neither-Location entries", TranslateTests.NormalizeLocations_RejectsNeitherLocationNorLink),
        ("normalizeLocations rejects negative and fractional coordinates", TranslateTests.NormalizeLocations_RejectsNegativeAndFractionalCoordinates),
        ("normalizeHover handles null and missing", TranslateTests.NormalizeHover_NullAndMissing),
        ("normalizeHover keeps MarkupContent and ranges", TranslateTests.NormalizeHover_MarkupContentKeepsRange),
        ("normalizeHover renders MarkedString forms", TranslateTests.NormalizeHover_MarkedStringForms),
        ("normalizeHover rejects malformed payloads", TranslateTests.NormalizeHover_RejectsMalformedPayloads),
        // Connection (§14.5).
        ("connection request/response round trip exposes a pid", ConnectionTests.RequestResponse_RoundTripAndExposesPid),
        ("connection forwards explicit env entries to the child", ConnectionTests.ForwardsExplicitEnvToChild),
        ("connection scrubs ambient DSH_ facts before merging env", ConnectionTests.ScrubAmbientDshFacts_BeforeMergingExplicitEnv),
        ("connection rejects a request on a server error response", ConnectionTests.ErrorResponse_RejectsRequest),
        ("terminating an already-closed child is a teardown race", ConnectionTests.Terminate_AlreadyClosedChild_IsTeardownRace),
        ("connection answers workspace/configuration from static config", ConnectionTests.AnswersWorkspaceConfigurationFromStaticConfig),
        ("connection drops a server notification without replying", ConnectionTests.DropsServerNotificationWithoutReply),
        ("connection sends an error response when the server-request handler rejects", ConnectionTests.ErrorResponse_WhenServerRequestHandlerRejects),
        ("connection tolerates garbage bytes before initialize", ConnectionTests.GarbageBytesBeforeInitialize_AreTolerated),
        ("connection rejects a request after the process closes", ConnectionTests.Request_AfterProcessCloses_Rejects),
        ("connection cancel after close is a no-op", ConnectionTests.Cancel_AfterClose_IsNoOp),
        ("connection caps the retained stderr tail", ConnectionTests.StderrTail_CappedAtMaxStderrBytes),
        ("connection rejects a request when the command cannot be spawned", ConnectionTests.SpawnFailure_RejectsRequest),
        ("connection kills the process and fails pending on a framing error", ConnectionTests.FramingError_KillsProcessAndFailsPending),
        ("connection ignores framed non-object messages", ConnectionTests.IgnoresFramedNonObjectMessages),
        ("connection drops a response for an unknown id", ConnectionTests.DropsResponseForUnknownId),
        ("connection caps the stderr tail across chunks", ConnectionTests.StderrTail_CappedAcrossChunks),
        ("connection caps the stderr tail by UTF-8 bytes", ConnectionTests.StderrTail_CappedByUtf8Bytes),
        ("connection falls back when an error response has no message", ConnectionTests.ErrorResponse_NoMessageString_FallsBack),
        ("connection rejects a pending request when the process exits mid-flight", ConnectionTests.PendingRequest_RejectsWhenProcessExitsMidFlight),
        ("connection rejects pending when stdin write fails but the process stays alive", ConnectionTests.StdinWriteFailure_RejectsPending_ProcessStaysAlive),
        ("connection ignores a frame that is neither a request nor a numeric-id response", ConnectionTests.IgnoresFrameNeitherRequestNorNumericIdResponse),
        // Instance (§14.6).
        ("instance answers workspace/configuration per item", InstanceTests.AnswersWorkspaceConfigurationPerItem),
        ("instance accepts lifecycle registerCapability", InstanceTests.AcceptsLifecycleRegisterCapability),
        ("instance rejects applyEdit but keeps serving", InstanceTests.RejectsApplyEditButKeepsServing),
        ("instance rejects an unknown server request but keeps serving", InstanceTests.RejectsUnknownServerRequestButKeepsServing),
        ("instance sends includeDeclaration for references", InstanceTests.References_SendsIncludeDeclaration),
        ("instance rejects a query aborted before it starts", InstanceTests.PreAbortedQuery_Rejects),
        ("instance cancels an in-flight request on abort", InstanceTests.CancelsInFlightRequestOnAbort_AndRejects),
        ("instance terminates when the server ignores cancel past the grace", InstanceTests.TerminatesInstance_WhenServerIgnoresCancelPastGrace),
        ("instance resolves the cancel grace when the server honors cancellation", InstanceTests.ResolvesCancelGrace_WhenServerHonorsCancelRequest),
        ("instance observes abort during a slow initialize handshake", InstanceTests.ObservesAbort_DuringSlowInitializeHandshake),
        ("instance terminates when abort interrupts a backpressured didOpen write", InstanceTests.Terminates_WhenAbortInterruptsBackpressuredDidOpenWrite),
        ("instance terminates when stdin fails during the didOpen write", InstanceTests.Terminates_WhenStdinFailsDuringDidOpenWrite),
        ("instance awaits process exit before rejecting a request write failure", InstanceTests.AwaitsProcessExit_BeforeRejectingRequestWriteFailure),
        ("instance rejects when the server lacks the operation capability", InstanceTests.Rejects_WhenServerLacksOperationCapability),
        ("instance propagates a server error response with a live signal", InstanceTests.PropagatesServerErrorResponse_EvenWithLiveSignal),
        ("instance keeps the settled result but tears down when didClose cannot be written", InstanceTests.KeepsSettledResult_ButTearsDown_WhenDidCloseCannotBeWritten),
        ("instance lets the server finish protocol exit before escalation", InstanceTests.Dispose_LetsServerFinishProtocolExit),
        ("instance dispose is idempotent", InstanceTests.Dispose_IsIdempotent),
        ("instance rejects a query after disposal", InstanceTests.Query_AfterDispose_RejectsLspDisposed),
        ("instance reports dead after the process closes", InstanceTests.Dead_AfterProcessCloses),
        ("instance escalates to kill when the server ignores shutdown", InstanceTests.Dispose_EscalatesToKill_WhenServerIgnoresShutdownAndSigterm),
        ("instance awaits a surviving process-tree helper on concurrent disposal", InstanceTests.Dispose_AwaitsSurvivingProcessTreeHelper),
        ("instance carries a non-Error abort reason as a generic aborted error", InstanceTests.NonErrorAbortReason_BecomesGenericAbortedError),
        // Tool parsing and pure rendering (§14.8).
        ("parseLspArgs converts one-based coordinates to zero-based", ToolRenderTests.ParseLspArgs_ConvertsOneBasedToZeroBased),
        ("parseLspArgs rejects an unknown operation", ToolRenderTests.ParseLspArgs_RejectsUnknownOperation),
        ("parseLspArgs rejects an empty file_path", ToolRenderTests.ParseLspArgs_RejectsEmptyFilePath),
        ("parseLspArgs rejects non-positive coordinates", ToolRenderTests.ParseLspArgs_RejectsNonPositiveCoordinates),
        ("formatLocations groups by file with one-based entries", ToolRenderTests.FormatLocations_GroupsByFileWithOneBasedEntries),
        ("formatLocations appends the omission marker inside the complete cap", ToolRenderTests.FormatLocations_AppendsOmissionMarker_InsideCompleteCap),
        ("formatLocations renders the no-results line", ToolRenderTests.FormatLocations_NoResultsLine),
        ("formatHover renders no-hover-information for null", ToolRenderTests.FormatHover_NullNoHoverInformation),
        ("formatHover keeps the truncation marker inside the cap", ToolRenderTests.FormatHover_TruncationMarkerInsideCap),
        ("renderUri renders workspace-relative, absolute, and verbatim URIs", ToolRenderTests.RenderUri_WorkspaceRelative_Inside_And_Absolute_Outside_And_Verbatim_NonFile),
        ("presentLspCall carries the operation and cursor in the title", ToolRenderTests.PresentLspCall_TitleCarriesOperationAndCursor),
        ("the mounted lsp tool queries and renders the recording", ToolLspTests.ToolLsp_QueriesThroughTheService_AndRendersTheRecording),
        ("the service routes by the final extension", ToolLspTests.LspService_RoutesByFinalExtension),
    };

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
}
