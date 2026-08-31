namespace Dsh.Sdk.Protocol.Tests;

/// <summary>Zero-dependency console test runner for the SDK wire protocol.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("transport: a request round-trips params and result", TransportTests.RequestResponse_RoundTripsParamsAndResult),
        ("transport: a handler failure answers internal-error", TransportTests.HandlerFailure_AnswersInternalError),
        ("transport: a missing handler answers method-not-found", TransportTests.MissingHandler_AnswersMethodNotFound),
        ("transport: notifications are delivered with and without params", TransportTests.Notifications_AreDelivered_WithAndWithoutParams),
        ("transport: a notification without a handler is dropped", TransportTests.NotificationWithoutHandler_IsDropped),
        ("transport: malformed lines are ignored", TransportTests.MalformedLines_AreIgnored),
        ("transport: cancellation removes the pending request", TransportTests.Cancellation_RemovesThePendingRequest),
        ("transport: closing rejects pending requests", TransportTests.Close_RejectsPendingRequests),
        ("transport: the peer ending its input fails pending requests", TransportTests.InputEnd_FailsPendingRequests),
        ("types: the method names and server identity are wire-stable", SdkProtocolTypesTests.TheMethodNamesAndServerIdentity_AreWireStable),
        ("types: the request and result records carry their fields", SdkProtocolTypesTests.TheRequestAndResultRecords_CarryTheirFields),
        ("types: the notification records carry their fields", SdkProtocolTypesTests.TheNotificationRecords_CarryTheirFields),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                var watchdog = Task.Run(run);
                if (!watchdog.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new AssertionException("TIMEOUT (test hung)");
                }
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
