using System.Text.Json;
using Cordis.Core;
using Dsh.Webhook;

namespace Dsh.Webhook.Tests;

/// <summary>One booted webhook spine: context and runtime.</summary>
public sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }

    public required WebhookRuntime Webhook { get; init; }

    /// <summary>Boot the spine.</summary>
    public static Harness Create(WebhookRuntimeConfig? config = null)
    {
        var ctx = new Context();
        var webhook = new WebhookRuntime(ctx, config);
        return new Harness { Ctx = ctx, Webhook = webhook };
    }

    /// <summary>Dispose the context (unwinding every effect).</summary>
    public void Dispose() => Ctx.Dispose();
}

/// <summary>One delivery with a detached JSON object event.</summary>
public static class Deliveries
{
    public static VerifiedWebhookDelivery GitHub(string deliveryId = "d-1")
        => new(
            "github",
            new WebhookSourceId("primary-github"),
            new WebhookDeliveryId(deliveryId),
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["name"] = "push",
                ["payload"] = new Dictionary<string, object?> { ["ref"] = "refs/heads/main" },
            }),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

/// <summary>Registry, dispatch, containment, and teardown behavior of <see cref="WebhookRuntime"/>.</summary>
public static class WebhookRuntimeTests
{
    private static WebhookRule RecordingRule(
        string id,
        string kind,
        List<string> log,
        Func<VerifiedWebhookDelivery, CancellationToken, Task<WebhookSessionRequest?>>? run = null)
        => new(new WebhookRuleId(id), kind, async (delivery, signal) =>
        {
            log.Add($"{id}:{delivery.Kind}:{delivery.DeliveryId}");
            return run is null ? null : await run(delivery, signal);
        });

    public static void Register_Dispatch_ReachesOnlyMatchingKind()
    {
        using var harness = Harness.Create();
        var log = new List<string>();
        var github = harness.Webhook.Register(RecordingRule("r1", "github", log));
        var gitlab = harness.Webhook.Register(RecordingRule("r2", "gitlab", log));
        harness.Webhook.Dispatch(Deliveries.GitHub());
        Assert.WaitUntil(() => log.Count == 1, message: "only the github rule ran");
        Assert.Equal("r1:github:d-1", log[0]);
        github.Dispose();
        gitlab.Dispose();
    }

    public static void Register_DuplicateId_FailsLoud()
    {
        using var harness = Harness.Create();
        var first = harness.Webhook.Register(RecordingRule("dup", "github", new List<string>()));
        var error = Assert.Throws<ArgumentException>(() => harness.Webhook.Register(RecordingRule("dup", "gitlab", new List<string>())));
        Assert.Contains("already registered", error.Message, "the duplicate id is named");
        first.Dispose();
    }

    public static void Register_EmptyIdOrKind_IsRejected()
    {
        using var harness = Harness.Create();
        Assert.Throws<ArgumentException>(() => new WebhookRule(new WebhookRuleId(""), "github", (_, _) => Task.FromResult<WebhookSessionRequest?>(null)));
        Assert.Throws<ArgumentException>(() => new WebhookRule(new WebhookRuleId("ok"), " ", (_, _) => Task.FromResult<WebhookSessionRequest?>(null)));
    }

    public static void Dispatch_MalformedDelivery_FailsLoud()
    {
        using var harness = Harness.Create();
        Assert.Throws<ArgumentException>(() => harness.Webhook.Dispatch(Deliveries.GitHub() with { Kind = " " }));
        Assert.Throws<ArgumentException>(() => harness.Webhook.Dispatch(Deliveries.GitHub() with { Source = new WebhookSourceId("") }));
        Assert.Throws<ArgumentException>(() => harness.Webhook.Dispatch(Deliveries.GitHub() with { DeliveryId = new WebhookDeliveryId("") }));
        Assert.Throws<ArgumentException>(() => harness.Webhook.Dispatch(Deliveries.GitHub() with { ReceivedAt = -1 }));
    }

    public static void Dispatch_DetachesTheEvent_FromTheCallerDocument()
    {
        using var harness = Harness.Create();
        VerifiedWebhookDelivery? seen = null;
        using (var rule = harness.Webhook.Register(new WebhookRule(new WebhookRuleId("r"), "github", (delivery, _) =>
        {
            seen = delivery;
            return Task.FromResult<WebhookSessionRequest?>(null);
        })))
        {
            var document = JsonDocument.Parse("{\"z\":1}");
            var delivery = new VerifiedWebhookDelivery(
                "github", new WebhookSourceId("s"), new WebhookDeliveryId("d"),
                document.RootElement, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            harness.Webhook.Dispatch(delivery); // snapshots the event while the document is alive
            document.Dispose(); // the caller-owned document is gone before the rule reads
            Assert.WaitUntil(() => seen is not null, message: "the rule ran");
            Assert.Equal(JsonValueKind.Object, seen!.Event.ValueKind, "the event survived the caller document disposal");
            Assert.Equal(1, seen.Event.GetProperty("z").GetInt32(), "the detached event carries the payload");
        }
    }

    public static void ThrowingRule_IsContained_AndOtherRulesStillRun()
    {
        using var harness = Harness.Create();
        var log = new List<string>();
        var throwing = harness.Webhook.Register(new WebhookRule(new WebhookRuleId("boom"), "github", (_, _) => throw new InvalidOperationException("exploded")));
        var ok = harness.Webhook.Register(RecordingRule("ok", "github", log));
        harness.Webhook.Dispatch(Deliveries.GitHub()); // must not throw
        Assert.WaitUntil(() => log.Count == 1, message: "the healthy rule ran despite the throwing one");
        Assert.Equal("ok:github:d-1", log[0]);
        throwing.Dispose();
        ok.Dispose();
    }

    public static void RuleReturningRequest_IsDeliveredToTheMountedAction()
    {
        var requests = new List<(VerifiedWebhookDelivery Delivery, WebhookRuleId RuleId, WebhookSessionRequest Request)>();
        using var harness = Harness.Create(new WebhookRuntimeConfig(new RecordingAction(requests)));
        using var rule = harness.Webhook.Register(new WebhookRule(new WebhookRuleId("creator"), "github", (_, _) =>
            Task.FromResult<WebhookSessionRequest?>(new WebhookSessionRequest(
                Path.GetFullPath("."), "title", "prompt", "agent", "permission"))));
        harness.Webhook.Dispatch(Deliveries.GitHub());
        Assert.WaitUntil(() => requests.Count == 1, message: "the action received the request");
        Assert.Equal("creator", requests[0].RuleId.Value);
        Assert.Equal("title", requests[0].Request.Title);
    }

    public static void RuleReturningRequest_WithNoAction_FailsTheInvocationLoudly()
    {
        using var harness = Harness.Create();
        var log = new List<string>();
        using var rule = harness.Webhook.Register(RecordingRule("creator", "github", log, (_, _) =>
            Task.FromResult<WebhookSessionRequest?>(new WebhookSessionRequest(
                Path.GetFullPath("."), "t", "p", "a", "perm"))));
        harness.Webhook.Dispatch(Deliveries.GitHub()); // must not throw
        Assert.WaitUntil(() => harness.Ctx.Logger.Buffer.Any(message =>
            message.Args.Any(arg => arg?.ToString()?.Contains("no webhook session action is mounted") == true)),
            message: "the missing action failed the invocation loud");
    }

    public static void DisposingTheRegistration_AbortsAndDrains_InFlightInvocations()
    {
        using var harness = Harness.Create();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = false;
        var registration = harness.Webhook.Register(new WebhookRule(new WebhookRuleId("slow"), "github", async (delivery, signal) =>
        {
            ran = true;
            started.TrySetResult(true);
            using var registration_ = signal.Register(() => released.TrySetResult(true));
            await Task.Delay(TimeSpan.FromSeconds(30), signal);
            return null;
        }));
        harness.Webhook.Dispatch(Deliveries.GitHub());
        started.Task.Wait(TimeSpan.FromSeconds(5));
        Assert.True(ran, "the invocation started");
        registration.Dispose();
        Assert.True(released.Task.Wait(TimeSpan.FromSeconds(5)), "disposal cancelled the invocation");
        Assert.True(harness.Ctx.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)), "context teardown completes with the invocation drained");
    }

    public static void Dispatch_WhileClosing_FailsLoud()
    {
        using var harness = Harness.Create();
        harness.Ctx.Dispose();
        Assert.Throws<InvalidOperationException>(() => harness.Webhook.Dispatch(Deliveries.GitHub()));
    }

    private sealed class RecordingAction : IWebhookSessionAction
    {
        private readonly List<(VerifiedWebhookDelivery Delivery, WebhookRuleId RuleId, WebhookSessionRequest Request)> _requests;

        public RecordingAction(List<(VerifiedWebhookDelivery Delivery, WebhookRuleId RuleId, WebhookSessionRequest Request)> requests)
        {
            _requests = requests;
        }

        public Task RunAsync(VerifiedWebhookDelivery delivery, WebhookRuleId ruleId, WebhookSessionRequest request, CancellationToken signal)
        {
            _requests.Add((delivery, ruleId, request));
            return Task.CompletedTask;
        }
    }
}
