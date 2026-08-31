namespace Dsh.Web.Host.Tests;

/// <summary>Zero-dependency console test runner for the Phase-5 web host.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("registry: register and dispatch returns the result", RpcRegistryTests.Register_AndDispatch_ReturnsTheResult),
        ("registry: an unknown endpoint settles method-not-found", RpcRegistryTests.UnknownEndpoint_SettlesMethodNotFound),
        ("registry: a throwing handler settles internal", RpcRegistryTests.ThrowingHandler_SettlesInternal_NotACarrierException),
        ("registry: cancellation settles cancelled", RpcRegistryTests.Cancellation_SettlesCancelled),
        ("registry: a duplicate endpoint fails loud", RpcRegistryTests.DuplicateEndpoint_FailsLoud),
        ("registry: an endpoint without namespace is rejected", RpcRegistryTests.EndpointWithoutNamespace_IsRejected),
        ("registry: disposal withdraws the method", RpcRegistryTests.DisposingTheRegistration_WithdrawsTheMethod),
        ("registry: context disposal withdraws every method", RpcRegistryTests.ContextDisposal_WithdrawsEveryMethod),
        ("host: boots and serves the mapped endpoints", WebHostTests.BootsAndServesTheMappedEndpoints),
        ("host: hub invoke round-trips through the registry", WebHostTests.HubInvoke_RoundTripsThroughTheRegistry),
        ("host: hub invoke settles method-not-found on the wire", WebHostTests.HubInvoke_UnknownEndpoint_SettlesMethodNotFound),
        ("host: stop closes the listener", WebHostTests.Stop_ClosesTheListener),
        ("http: unary round-trip echoes rpcId and value", GatewayHttpTests.UnaryRoundTrip_EchoesRpcIdAndValue),
        ("http: unknown endpoint settles invocation-unavailable with HTTP 200", GatewayHttpTests.UnknownEndpoint_SettlesInvocationUnavailable_WithHttp200),
        ("http: invalid envelope settles bad-request with fallback rpcId", GatewayHttpTests.InvalidEnvelope_SettlesBadRequest_WithFallbackRpcId),
        ("http: method mismatch settles bad-request", GatewayHttpTests.MethodMismatch_SettlesBadRequest),
        ("http: non-JSON content type answers 415", GatewayHttpTests.NonJsonContentType_Answers415),
        ("http: non-POST answers 404", GatewayHttpTests.NonPostMethod_Answers404),
        ("http: invalid segments answer 404", GatewayHttpTests.InvalidSegment_Answers404),
        ("mux: events stream sends ready then live emits", GatewayMuxTests.EventsStream_SendsReadyThenLiveEmits),
        ("mux: unknown endpoint answers an error frame", GatewayMuxTests.UnknownEndpoint_AnswersErrorFrame),
        ("mux: cancel ends the logical stream", GatewayMuxTests.Cancel_EndsTheLogicalStream),
        ("mux: cancel ends a registry stream quietly", GatewayMuxTests.Cancel_EndsARegistryStreamQuietly),
        ("wire: SessionWireEvent lifts the envelope out of data", SessionRemotesTests.WireProjection_LiftsEnvelopeOutOfData),
        ("session: page returns windowed records", SessionRemotesTests.Page_ReturnsWindowedRecords_OverRealTurns),
        ("session: page unknown session settles session-not-found", SessionRemotesTests.Page_UnknownSession_SettlesSessionNotFound),
        ("session: follow sends snapshot then gap-checked live events", () => SessionRemotesTests.Follow_SendsSnapshotThenLiveEvents_OverRealTurns().GetAwaiter().GetResult()),
        ("session: control baseline shows queued messages then queue deltas", () => SessionControlTests.Control_BaselineShowsQueuedMessages_ThenQueueDelta().GetAwaiter().GetResult()),
        ("session: control jobs baseline and delta", () => SessionControlTests.Control_JobsBaselineAndDelta().GetAwaiter().GetResult()),
        ("session: control projections baseline", () => SessionControlTests.Control_ProjectionsBaseline().GetAwaiter().GetResult()),
        ("session: control projection deltas", () => SessionControlTests.Control_ProjectionDeltas().GetAwaiter().GetResult()),
        ("session: control ends on cancellation", () => SessionControlTests.Control_EndsOnCancellation().GetAwaiter().GetResult()),
        ("store: queued counts project over inbox events", () => WebSessionStoreTests.Store_ProjectsQueuedCountsOverInboxEvents().GetAwaiter().GetResult()),
        ("store: running and summary project after a turn", () => WebSessionStoreTests.Store_ProjectsRunningAndSummaryAfterTurn().GetAwaiter().GetResult()),
        ("store: the last agent error projects and clears on activity", () => WebSessionStoreTests.Store_ProjectsTheLastAgentError_AndClearsOnActivity().GetAwaiter().GetResult()),
        ("settings: describe returns the redacted catalog", SettingsRemotesTests.Describe_ReturnsRedactedCatalog),
        ("settings: update merges and answers the new view", SettingsRemotesTests.Update_MergesAndAnswersTheNewView),
        ("settings: stale revision settles conflict", SettingsRemotesTests.Update_StaleRevision_SettlesConflict),
        ("settings: unknown namespace settles rejected", SettingsRemotesTests.Update_UnknownNamespace_SettlesRejected),
        ("settings: replace replaces wholesale", SettingsRemotesTests.Replace_ReplacesWholesale),
        ("settings: mutate applies path ops and answers the new view", SettingsRemotesTests.Mutate_AppliesPathOpsAndAnswersTheNewView),
        ("settings: mutate bad op shape settles bad-request", SettingsRemotesTests.Mutate_BadOpShape_SettlesBadRequest),
        ("settings: mutate unknown namespace settles rejected", SettingsRemotesTests.Mutate_UnknownNamespace_SettlesRejected),
        ("settings: describe without provider settles internal", SettingsRemotesTests.Describe_WithoutProvider_SettlesInternal),
        ("settings: open document materializes and opens", SettingsOpenersTests.OpenSettingsDocument_MaterializesAndOpens),
        ("settings: open document without a local document settles internal", SettingsOpenersTests.OpenSettingsDocument_NoLocalDocument_SettlesInternal),
        ("settings: open document opener failure settles internal", SettingsOpenersTests.OpenSettingsDocument_OpenerFailure_SettlesInternal),
        ("settings: canOpenAgentPresetDirectory answers the deployment fact", SettingsOpenersTests.CanOpenAgentPresetDirectory_AnswersTheDeploymentFact),
        ("settings: open preset directory empty id settles bad-request", SettingsOpenersTests.OpenAgentPresetDirectory_EmptyId_SettlesBadRequest),
        ("settings: open preset directory without service settles not-found", SettingsOpenersTests.OpenAgentPresetDirectory_NoPresetService_SettlesNotFound),
        ("settings: open preset directory missing preset settles not-found", SettingsOpenersTests.OpenAgentPresetDirectory_MissingPreset_SettlesNotFound),
        ("settings: open preset directory without native opener returns the path", SettingsOpenersTests.OpenAgentPresetDirectory_WithoutNativeOpener_ReturnsThePath),
        ("settings: open preset directory opens through the fake", SettingsOpenersTests.OpenAgentPresetDirectory_OpensThroughTheFake),
        ("credentials: describe returns per-ref facts", CredentialsRemotesTests.Describe_ReturnsPerRefFacts),
        ("credentials: describe rejects over 64 refs", CredentialsRemotesTests.Describe_RejectsOver64Refs),
        ("credentials: describe rejects bad grammar", CredentialsRemotesTests.Describe_RejectsBadGrammar),
        ("credentials: set stores the value", CredentialsRemotesTests.Set_StoresTheValue),
        ("credentials: set rejects empty value", CredentialsRemotesTests.Set_RejectsEmptyValue),
        ("credentials: shadowed set settles rejected", CredentialsRemotesTests.Set_ShadowedByEnvironment_SettlesRejected),
        ("credentials: unset removes the reference", CredentialsRemotesTests.Unset_RemovesTheReference),
        ("workspace: create answers the view", WorkspaceRemotesTests.Create_NewDirectory_AnswersTheView),
        ("workspace: same path answers not-created", WorkspaceRemotesTests.Create_SamePathAgain_AnswersNotCreated),
        ("workspace: missing path settles invalid-path", WorkspaceRemotesTests.Create_MissingPath_SettlesInvalidPath),
        ("workspace: a second workspace succeeds", WorkspaceRemotesTests.Create_SecondWorkspace_Succeeds),
        ("workspace: missing path arg settles bad-request", WorkspaceRemotesTests.Create_WithoutPathArg_SettlesBadRequest),
        ("workspace: rename updates the title and settles the wire codes", WorkspaceRemotesTests.Rename_UpdatesTitle_AndSettlesTheWireCodes),
        ("workspace: delete removes and settles not-found when absent", WorkspaceRemotesTests.Delete_Removes_SettlesNotFoundWhenAbsent),
        ("workspace: insertBefore moves the order and settles not-found", WorkspaceRemotesTests.InsertBefore_MovesOrder_SettlesNotFound),
        ("workspace: insertSessionBefore moves membership and settles move-invalid", WorkspaceRemotesTests.InsertSessionBefore_MovesMembership_SettlesMoveInvalid),
        ("workspace: archiveSession adds to the archive and settles session-not-found", WorkspaceRemotesTests.ArchiveSession_AddsToArchive_SettlesSessionNotFound),
        ("workspace: follow sends baseline then deltas", () => WorkspaceRemotesTests.Follow_SendsBaselineThenDeltas().GetAwaiter().GetResult()),
        ("directoryPicker: pick answers unavailable", DirectoryPickerTests.Pick_AnswersUnavailable),
        ("directoryPicker: list answers unavailable", DirectoryPickerTests.List_AnswersUnavailable),
        ("directoryPicker: bad create name settles bad-request", DirectoryPickerTests.CreateDirectory_BadName_SettlesBadRequest),
        ("directoryPicker: valid create name answers unavailable", DirectoryPickerTests.CreateDirectory_ValidName_AnswersUnavailable),
        ("fence: index without token settles 401", FenceTests.Index_WithoutToken_Settles401),
        ("fence: token exchange mints the cookie then serves the index", FenceTests.TokenExchange_MintsCookie_ThenIndexServes),
        ("fence: wrong token settles 401", FenceTests.Index_WithWrongToken_Settles401),
        ("fence: api without cookie settles 401", FenceTests.Api_WithoutCookie_Settles401),
        ("fence: api with cookie round-trips", FenceTests.Api_WithCookie_RoundTrips),
        ("fence: untrusted host settles 403", FenceTests.UntrustedHost_Settles403),
        ("fence: cross-site origin settles 403", FenceTests.CrossSiteOrigin_Settles403),
        ("fence: tampered cookie settles 401", FenceTests.TamperedCookie_Settles401),
        ("fence: hub negotiate without cookie settles 401", FenceTests.HubNegotiate_WithoutCookie_Settles401),
        ("fence: mux path gated then reaches the handler", FenceTests.MuxPath_WithoutCookie_Settles401_WithCookieReachesHandler),
        ("fence: cookie is bound to its authority", FenceTests.Cookie_IsBoundToItsAuthority),
        ("fence: persistent secret survives a host restart", FenceTests.PersistentSecret_CookiesSurviveRestart),
        ("fence: declared trusted host passes trust, undeclared still 403", FenceTests.TrustedHost_MatchesDeclaredAuthority_UntrustedStill403),
        ("fence: port-less trustedHosts entry matches any port", FenceTests.TrustedHost_PortlessEntry_MatchesAnyPort),
        ("fence: explicit-port trustedHosts entry matches only that port", FenceTests.TrustedHost_ExplicitPortEntry_MatchesOnlyThatPort),
        ("fence: explicit default port entry matches default-port requests", FenceTests.TrustedHost_ExplicitDefaultPort_MatchesDefaultPortRequests),
        ("fence: trusted authority round-trips its own cookie", FenceTests.TrustedHost_CookieRoundTrip_UnderTheDeclaredAuthority),
        ("fence: malformed trustedHosts entries fail the boot", FenceTests.TrustedHosts_MalformedEntry_FailsLoud),
        ("settings: open preset directory system preset settles read-only", SettingsOpenersTests.OpenAgentPresetDirectory_SystemPreset_SettlesReadOnly),
        ("locale: English resolves its keys and unknown keys render as themselves", WebLocaleTests.EnglishResolvesItsOwnKeys_UnknownKeysRenderAsThemselves),
        ("locale: Chinese resolves its own keys", WebLocaleTests.ChineseResolvesItsOwnKeys),
        ("locale: negotiation picks the first supported language", WebLocaleTests.NegotiatePicksTheFirstSupportedLanguage),
        ("locale: an unknown locale id falls back to English", WebLocaleTests.ForUnknownLanguageFallsBackToEnglish),
        ("locale: the shell prerenders in the negotiated locale", WebLocaleTests.ShellPrerendersInTheNegotiatedLocale),
        ("codec: generated encode produces the error vocabulary shape", RpcCodecTests.Encode_ProducesTheErrorVocabularyShape),
        ("codec: generated encode carries structured details", RpcCodecTests.Encode_CarriesStructuredDetails),
        ("codec: generated decode round-trips the encode", RpcCodecTests.TryDecode_RoundTripsEncode),
        ("codec: generated decode refuses malformed elements", RpcCodecTests.TryDecode_RejectsMalformedElements),
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




