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

