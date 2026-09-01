using System.Text.Json;

namespace Harness.Webhook;

/// <summary>Identifies one programmatic webhook rule.</summary>
public readonly record struct WebhookRuleId(string Value)
{
    public static implicit operator string(WebhookRuleId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Identifies one configured webhook adapter instance.</summary>
public readonly record struct WebhookSourceId(string Value)
{
    public static implicit operator string(WebhookSourceId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Identifies one provider delivery; the runtime assigns no deduplication semantics.</summary>
public readonly record struct WebhookDeliveryId(string Value)
{
    public static implicit operator string(WebhookDeliveryId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>
/// One authenticated and parsed provider delivery. The event is provider-normalized lossless JSON;
/// a provider adapter (such as the GitHub handler) supplies its own event spelling
/// (<c>{"name": ..., "payload": ...}</c> for GitHub). <see cref="ReceivedAt"/> is host receipt
/// time in Unix epoch milliseconds.
/// </summary>
public sealed record VerifiedWebhookDelivery(
    /// <summary>Provider family such as <c>github</c>.</summary>
    string Kind,
    /// <summary>Configured adapter instance such as <c>primary-github</c>.</summary>
    WebhookSourceId Source,
    /// <summary>Provider identity exposed as provenance, never as built-in deduplication state.</summary>
    WebhookDeliveryId DeliveryId,
    /// <summary>Provider-normalized lossless JSON.</summary>
    JsonElement Event,
    /// <summary>Host receipt time in Unix epoch milliseconds.</summary>
    long ReceivedAt);

/// <summary>Optional explicit model route and output cap for a webhook-created Agent.</summary>
public sealed record WebhookModelSelection(
    /// <summary>Registered provider route.</summary>
    string Provider,
    /// <summary>Provider-owned model id.</summary>
    string Model,
    /// <summary>Optional positive output-token cap.</summary>
    int? MaxTokens = null);

/// <summary>
/// The sole runtime action of the webhook seam: create and prompt one root Session. The C# port
/// validates the request exactly like the TS seam but does not create the Agent itself. The
/// session-creation action is the owning composition hook (see <see cref="IWebhookSessionAction"/>),
/// deferred until the Phase-5 web host mounts the full agent/workspace/preset spine.
/// </summary>
public sealed record WebhookSessionRequest(
    /// <summary>Existing local directory to resolve or create as a Workspace (absolute).</summary>
    string WorkspacePath,
    /// <summary>Explicit Session title.</summary>
    string Title,
    /// <summary>Non-empty initial text prompt.</summary>
    string Prompt,
    /// <summary>Agent composition mounted before publication.</summary>
    string AgentPreset,
    /// <summary>Sandbox and approval preset applied before prompt admission.</summary>
    string PermissionPreset,
    /// <summary>Optional explicit route; omission uses the complete current default.</summary>
    WebhookModelSelection? Model = null);

/// <summary>
/// Trusted code that optionally creates one Session for a delivery. The <see cref="Run"/> delegate
/// receives an immutable, snapshotted delivery and the registration lifetime signal; returning
/// <c>null</c> takes no action.
/// </summary>
public sealed class WebhookRule
{
    /// <summary>Create a rule; <paramref name="id"/> and <paramref name="kind"/> must be non-empty.</summary>
    public WebhookRule(WebhookRuleId id, string kind, Func<VerifiedWebhookDelivery, CancellationToken, Task<WebhookSessionRequest?>> run)
    {
        if (id.Value.Length == 0) throw new ArgumentException("webhook rule id must be a non-empty string", nameof(id));
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("webhook rule kind must be a non-empty string", nameof(kind));
        Id = id;
        Kind = kind;
        Run = run ?? throw new ArgumentNullException(nameof(run));
    }

    /// <summary>Globally unique diagnostic identity.</summary>
    public WebhookRuleId Id { get; }

    /// <summary>Provider kind this rule receives.</summary>
    public string Kind { get; }

    /// <summary>
    /// Run arbitrary trusted code and optionally request one Session.
    /// </summary>
    /// <param name="delivery">immutable authenticated provider data.</param>
    /// <param name="signal">cancelled when this registration or the runtime unloads.</param>
    /// <returns>one Session request, or <c>null</c> for no action.</returns>
    public Func<VerifiedWebhookDelivery, CancellationToken, Task<WebhookSessionRequest?>> Run { get; }
}

/// <summary>GitHub event values projected after signature verification.</summary>
public sealed record GitHubWebhookEvent(
    /// <summary>Raw <c>X-GitHub-Event</c> name such as <c>pull_request</c>.</summary>
    string Name,
    /// <summary>Signed JSON object exactly as parsed from the request body.</summary>
    JsonElement Payload);
