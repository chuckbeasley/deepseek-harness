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
        ("wire: SessionWireEvent lifts the envelope out of data", SessionRemotesTests.WireProjection_LiftsEnvelopeOutOfData),
        ("session: page returns windowed records", SessionRemotesTests.Page_ReturnsWindowedRecords_OverRealTurns),
        ("session: page unknown session settles session-not-found", SessionRemotesTests.Page_UnknownSession_SettlesSessionNotFound),
        ("session: follow sends snapshot then gap-checked live events", () => SessionRemotesTests.Follow_SendsSnapshotThenLiveEvents_OverRealTurns().GetAwaiter().GetResult()),
        ("settings: describe returns the redacted catalog", SettingsRemotesTests.Describe_ReturnsRedactedCatalog),
        ("settings: update merges and answers the new view", SettingsRemotesTests.Update_MergesAndAnswersTheNewView),
        ("settings: stale revision settles conflict", SettingsRemotesTests.Update_StaleRevision_SettlesConflict),
        ("settings: unknown namespace settles rejected", SettingsRemotesTests.Update_UnknownNamespace_SettlesRejected),
        ("settings: replace replaces wholesale", SettingsRemotesTests.Replace_ReplacesWholesale),
        ("settings: mutate applies path ops and answers the new view", SettingsRemotesTests.Mutate_AppliesPathOpsAndAnswersTheNewView),
        ("settings: mutate bad op shape settles bad-request", SettingsRemotesTests.Mutate_BadOpShape_SettlesBadRequest),
        ("settings: mutate unknown namespace settles rejected", SettingsRemotesTests.Mutate_UnknownNamespace_SettlesRejected),
        ("settings: describe without provider settles internal", SettingsRemotesTests.Describe_WithoutProvider_SettlesInternal),
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
        ("workspace: second open settles invalid-path", WorkspaceRemotesTests.Create_WhileAnotherOpen_SettlesInvalidPath),
        ("workspace: missing path arg settles bad-request", WorkspaceRemotesTests.Create_WithoutPath_SettlesBadRequest),
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




