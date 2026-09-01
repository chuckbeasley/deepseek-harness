using System.Text.Json;
using Harness.Cordis.Core;

namespace Harness.Webhook;

/// <summary>One effect-owned rule registration and the invocations that currently use it.</summary>
internal sealed class RuleRegistration
{
    public required WebhookRule Rule { get; init; }

    public required CancellationTokenSource Controller { get; init; }

    public HashSet<Task> Active { get; } = new();

    public bool Closing;
}

/// <summary>Mutable snapshot-observation state one dispatch collects while rules run.</summary>
internal static class WebhookDeliverySnapshot
{
    /// <summary>
    /// Validate and detach one delivery before sharing it across arbitrary rules: the shared
    /// <see cref="JsonElement"/> is cloned so a later mutation of the caller-owned document cannot
    /// change what rules observed.
    /// </summary>
    public static VerifiedWebhookDelivery Snapshot(VerifiedWebhookDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Kind.Trim().Length == 0)
        {
            throw new ArgumentException("webhook delivery kind must be a non-empty string", nameof(delivery));
        }
        if (delivery.Source.Value.Trim().Length == 0)
        {
            throw new ArgumentException("webhook delivery source must be a non-empty string", nameof(delivery));
        }
        if (delivery.DeliveryId.Value.Trim().Length == 0)
        {
            throw new ArgumentException("webhook delivery id must be a non-empty string", nameof(delivery));
        }
        if (delivery.ReceivedAt < 0)
        {
            throw new ArgumentException("webhook delivery receivedAt must be a non-negative value", nameof(delivery));
        }
        var detached = delivery.Event.Clone();
        return new VerifiedWebhookDelivery(delivery.Kind, delivery.Source, delivery.DeliveryId, detached, delivery.ReceivedAt);
    }
}

/// <summary>Configuration for the webhook runtime: the optional session-creation action.</summary>
public sealed record WebhookRuntimeConfig(IWebhookSessionAction? SessionAction = null);

/// <summary>
/// The webhook runtime (ctx.webhook): a registry of trusted programmatic rules, one dispatch fan
/// per delivery. Port of <c>@deepseek-ai/dsh-webhook</c>. Invocations are contained: a throwing
/// rule is logged and never fails the dispatch or another rule. Registration teardown hides the
/// rule, aborts its signal, and drains its active invocations before returning.
/// </summary>
public sealed class WebhookRuntime : Service, IWebhookService
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "webhook";

    private readonly object _gate = new();
    private readonly Dictionary<string, RuleRegistration> _rules = new(StringComparer.Ordinal);
    private readonly IWebhookSessionAction? _sessionAction;
    private bool _closing;

    /// <summary>Create and register the runtime under the <c>webhook</c> key.</summary>
    public WebhookRuntime(Context ctx, WebhookRuntimeConfig? config = null)
        : base(ctx, ServiceKey)
    {
        _sessionAction = config?.SessionAction;
    }

    /// <inheritdoc />
    public IDisposable Register(WebhookRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Ctx.Effect(() =>
        {
            lock (_gate)
            {
                if (_closing) throw new InvalidOperationException("webhook runtime is closing");
                if (_rules.ContainsKey(rule.Id.Value))
                {
                    throw new ArgumentException($"webhook rule \"{rule.Id}\" is already registered", nameof(rule));
                }
                var registration = new RuleRegistration
                {
                    Rule = rule,
                    Controller = new CancellationTokenSource(),
                };
                _rules.Add(rule.Id.Value, registration);
                return new ActionDisposer(() => DisposeRegistration(registration));
            }
        }, $"webhook.register(\"{rule.Id}\")");
    }

    /// <inheritdoc />
    public void Dispatch(VerifiedWebhookDelivery delivery)
    {
        var snapshot = WebhookDeliverySnapshot.Snapshot(delivery);
        RuleRegistration[] matching;
        lock (_gate)
        {
            if (_closing) throw new InvalidOperationException("webhook runtime is closing");
            matching = _rules.Values
                .Where(registration => !registration.Closing && registration.Rule.Kind == snapshot.Kind)
                .ToArray();
        }
        foreach (var registration in matching) StartInvocation(registration, snapshot);
    }

    /// <summary>Teardown: close the runtime and drain every registration.</summary>
    public override async ValueTask StopAsync()
    {
        RuleRegistration[] live;
        lock (_gate)
        {
            _closing = true;
            live = _rules.Values.ToArray();
        }
        foreach (var registration in live) await DisposeRegistrationAsync(registration).ConfigureAwait(false);
        await base.StopAsync();
    }

    /// <summary>Start one contained invocation and attach it to registration teardown.</summary>
    private void StartInvocation(RuleRegistration registration, VerifiedWebhookDelivery delivery)
    {
        Task tracked;
        lock (registration.Active)
        {
            tracked = InvokeAsync(registration, delivery);
            registration.Active.Add(tracked);
        }
        _ = ObserveAsync(registration, delivery, tracked);
    }

    /// <summary>Run the rule, then hold the session request to the mounted action.</summary>
    private async Task InvokeAsync(RuleRegistration registration, VerifiedWebhookDelivery delivery)
    {
        var signal = registration.Controller.Token;
        var request = await registration.Rule.Run(delivery, signal).ConfigureAwait(false);
        signal.ThrowIfCancellationRequested();
        if (request is null) return;
        var action = _sessionAction
            ?? throw new InvalidOperationException(
                $"webhook: rule \"{registration.Rule.Id}\" returned a Session request but no webhook session action is mounted");
        await action.RunAsync(delivery, registration.Rule.Id, request, signal).ConfigureAwait(false);
    }

    /// <summary>Contain one invocation: log its failure and detach it from the registration.</summary>
    private async Task ObserveAsync(RuleRegistration registration, VerifiedWebhookDelivery delivery, Task tracked)
    {
        try
        {
            await tracked.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var invocation = $"webhook: provider={delivery.Kind} source={delivery.Source} "
                + $"delivery={delivery.DeliveryId} rule={registration.Rule.Id}";
            if (registration.Controller.IsCancellationRequested)
            {
                Ctx.Logger.Debug($"{invocation} stopped after disposal: {error.Message}");
            }
            else
            {
                Ctx.Logger.Warn($"{invocation} failed: {error.Message}");
            }
        }
        finally
        {
            lock (registration.Active) registration.Active.Remove(tracked);
        }
    }

    /// <summary>Hide and abort one registration; the drain runs on the caller await path.</summary>
    private void DisposeRegistration(RuleRegistration registration)
    {
        lock (_gate) _rules.Remove(registration.Rule.Id.Value);
        registration.Closing = true;
        registration.Controller.Cancel();
    }

    /// <summary>Abort and drain one registration to quiescence.</summary>
    private async Task DisposeRegistrationAsync(RuleRegistration registration)
    {
        DisposeRegistration(registration);
        while (true)
        {
            Task[] pending;
            lock (registration.Active) pending = registration.Active.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync effect cleanups.</summary>
internal sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
