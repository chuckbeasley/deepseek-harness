namespace Harness.Webhook;

/// <summary>
/// Service Definition of the webhook capability seam (port of <c>@deepseek-ai/dsh-webhook</c>):
/// a fire-and-forget rule registry over verified provider deliveries. Adapters authenticate and
/// parse HTTP deliveries, then call <see cref="Dispatch"/>; every currently matching rule starts
/// one contained invocation that may request a Session. The seam owns the conversation with
/// providers and rules; it never owns the HTTP listener (the ingress provider does).
/// </summary>
public interface IWebhookService
{
    /// <summary>
    /// Register one trusted rule. One rule per id: two rules with the same id would make
    /// registrations and diagnostics ambiguous.
    /// </summary>
    /// <param name="rule">unique id, provider kind, and arbitrary callback.</param>
    /// <returns>an effect-owned disposer that hides the rule, aborts its in-flight invocations,
    /// and awaits their settlement.</returns>
    /// <exception cref="ArgumentException">when the id or kind is empty, or the id is already
    /// registered.</exception>
    /// <exception cref="InvalidOperationException">when the runtime is closing.</exception>
    IDisposable Register(WebhookRule rule);

    /// <summary>
    /// Start every currently matching rule and return before any callback settles.
    /// </summary>
    /// <param name="delivery">authenticated provider data; snapshotted before dispatch.</param>
    /// <exception cref="ArgumentException">when the delivery is malformed.</exception>
    /// <exception cref="InvalidOperationException">when the runtime is closing.</exception>
    void Dispatch(VerifiedWebhookDelivery delivery);
}

/// <summary>
/// The owning composition hook that creates one Session from a settled rule result. The TS seam
/// creates the Session inside the runtime; the C# port defers that integration (agent presets,
/// permission presets, workspace registry, session titles are later-wave seams) and requires the
/// action to be mounted explicitly. A rule whose result needs an action while none is mounted
/// fails the invocation loud instead of silently dropping the request.
/// </summary>
public interface IWebhookSessionAction
{
    /// <summary>
    /// Create and prompt one root Session for the delivery.
    /// </summary>
    /// <param name="delivery">the exact verified delivery used for provenance.</param>
    /// <param name="ruleId">the rule that returned the request.</param>
    /// <param name="request">the validated Session request.</param>
    /// <param name="signal">registration lifetime cancellation through publication.</param>
    Task RunAsync(
        VerifiedWebhookDelivery delivery,
        WebhookRuleId ruleId,
        WebhookSessionRequest request,
        CancellationToken signal);
}
