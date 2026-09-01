namespace Harness.Webhook.Tests;

/// <summary>Zero-dependency console test runner for the webhook capability seam.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("registry: dispatch reaches only the matching kind", WebhookRuntimeTests.Register_Dispatch_ReachesOnlyMatchingKind),
        ("registry: a duplicate rule id fails loud", WebhookRuntimeTests.Register_DuplicateId_FailsLoud),
        ("registry: an empty id or kind is rejected", WebhookRuntimeTests.Register_EmptyIdOrKind_IsRejected),
        ("dispatch: a malformed delivery fails loud", WebhookRuntimeTests.Dispatch_MalformedDelivery_FailsLoud),
        ("dispatch: the event is detached from the caller document", WebhookRuntimeTests.Dispatch_DetachesTheEvent_FromTheCallerDocument),
        ("dispatch: a throwing rule is contained and other rules still run", WebhookRuntimeTests.ThrowingRule_IsContained_AndOtherRulesStillRun),
        ("dispatch: a session request is delivered to the mounted action", WebhookRuntimeTests.RuleReturningRequest_IsDeliveredToTheMountedAction),
        ("dispatch: a session request with no action fails the invocation loud", WebhookRuntimeTests.RuleReturningRequest_WithNoAction_FailsTheInvocationLoudly),
        ("teardown: disposing the registration aborts and drains in-flight invocations", WebhookRuntimeTests.DisposingTheRegistration_AbortsAndDrains_InFlightInvocations),
        ("dispatch: while closing fails loud", WebhookRuntimeTests.Dispatch_WhileClosing_FailsLoud),
        ("github handler: a non-POST method answers 405 with Allow", GitHubHandlerTests.MethodNotPost_Answers405WithAllow),
        ("github handler: content type must be application/json", GitHubHandlerTests.ContentTypeMustBeJson),
        ("github handler: missing headers answer 400", GitHubHandlerTests.MissingHeaders_Answer400),
        ("github handler: a body over the ceiling answers 413", GitHubHandlerTests.BodyOverTheCeiling_Answers413),
        ("github handler: invalid UTF-8 answers 400", GitHubHandlerTests.InvalidUtf8Body_Answers400),
        ("github handler: an unavailable secret answers 503", GitHubHandlerTests.SecretUnavailable_Answers503),
        ("github handler: a wrong signature answers 401", GitHubHandlerTests.WrongSignature_Answers401),
        ("github handler: a malformed signature answers 401", GitHubHandlerTests.MalformedSignature_Answers401),
        ("github handler: a valid signature dispatches and answers 202", GitHubHandlerTests.ValidSignature_DispatchesAndAnswers202),
        ("github handler: invalid JSON answers 400", GitHubHandlerTests.InvalidJson_Answers400),
        ("github handler: a dispatch failure answers 503", GitHubHandlerTests.DispatchFailure_Answers503),
        ("ingress: a signed POST reaches the rule and answers 202", IngressTests.SignedPost_ReachesTheRule_AndAnswers202),
        ("ingress: a bad signature answers 401 over real HTTP", IngressTests.BadSignature_Answers401),
        ("ingress: an oversized body answers 413", IngressTests.OversizedBody_Answers413),
        ("ingress: stopping the ingress closes the listener", IngressTests.StoppingTheIngress_ClosesTheListener),
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
