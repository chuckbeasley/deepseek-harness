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
