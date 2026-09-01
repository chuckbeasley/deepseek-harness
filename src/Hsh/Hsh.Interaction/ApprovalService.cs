using Harness.Cordis.Core;
using Harness.Session;

namespace Harness.Interaction;

/// <summary>
/// The approval capability seam (port of the TS <c>user-approval</c>): session policy applied
/// before the answerers, and every ask/outcome pair audited to the requesting session's durable
/// log. The answerers compose on the <c>approval/request</c> waterfall — a TUI dialog, a web
/// approval surface, or a deterministic policy — and an ask with no answerer fails closed
/// (<see cref="ApprovalOutcome.Unavailable"/>). <see cref="ApprovalOutcome.AllowedOnce"/> is the
/// only grant.
/// </summary>
public sealed class ApprovalService : Service
{
    /// <summary>The waterfall answerers compose on.</summary>
    public const string RequestEvent = "approval/request";

    /// <summary>Every outcome, for runtime normalization of answerer returns.</summary>
    private static readonly ApprovalOutcome[] Outcomes =
    {
        ApprovalOutcome.AllowedOnce, ApprovalOutcome.Rejected, ApprovalOutcome.Cancelled, ApprovalOutcome.Unavailable,
    };

    private readonly ApprovalPolicy _policy;

    /// <summary>Create and register the service as <c>approval</c>.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="policy">the deployment default for sessions without an override.</param>
    public ApprovalService(Context ctx, ApprovalPolicy policy = ApprovalPolicy.Ask)
        : base(ctx, "approval")
    {
        _policy = policy;
        InteractionEventTypes.Register();
    }

    /// <summary>
    /// Ask the composed answerers to decide one request. The audit pair must be turn-enclosed: an
    /// idle ask (no open turn in the session log) rejects before appending anything, because a bare
    /// event between turns is crash-tail garbage on reload. The answerer phase always produces an
    /// outcome: an aborted token yields <see cref="ApprovalOutcome.Cancelled"/>, a missing or
    /// throwing answerer yields <see cref="ApprovalOutcome.Unavailable"/> (fail closed), and a rogue
    /// non-vocabulary return is normalized to <see cref="ApprovalOutcome.Unavailable"/>.
    /// </summary>
    /// <param name="request">the pending decision.</param>
    /// <returns>the closed outcome; <see cref="ApprovalOutcome.AllowedOnce"/> is the only grant.</returns>
    /// <exception cref="InvalidOperationException">when no turn is open in the session log.</exception>
    public async Task<ApprovalOutcome> AskAsync(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = request.Agent.Session;
        if (!HasOpenTurn(session.Events))
        {
            throw new InvalidOperationException(
                "approval.request() outside an open turn: the approval/asked + approval/decided audit pair "
                + "must be turn-enclosed (a bare event between turns is crash-tail garbage on reload). "
                + "Ask from inside the turn that needs the decision.");
        }
        var id = Guid.NewGuid().ToString("D");
        session.Append(new ApprovalAskedEvent
        {
            Id = id,
            ToolName = request.ToolName,
            CallId = request.CallId,
            Reason = request.Reason,
        });
        var outcome = await DecideAsync(request, session);
        session.Append(new ApprovalDecidedEvent { Id = id, Outcome = outcome });
        return outcome;
    }

    /// <summary>
    /// Switch one live agent's session override. The last <see cref="ApprovalPolicyEvent"/> in the
    /// log is the session's effective policy; an unchanged value appends nothing.
    /// </summary>
    public void SetPolicy(Harness.Agent.Agent agent, ApprovalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (EffectivePolicy(agent.Session) == policy) return;
        agent.Session.Append(new ApprovalPolicyEvent { Policy = policy });
    }

    /// <summary>The session's effective policy: its own override fold, else the configured default.</summary>
    public ApprovalPolicy EffectivePolicy(Harness.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (var index = session.Events.Count - 1; index >= 0; index--)
        {
            if (session.Events[index] is ApprovalPolicyEvent policyEvent) return policyEvent.Policy;
        }
        return _policy;
    }

    /// <summary>Whether the log currently sits inside an open turn (a turn/start not yet closed).</summary>
    private static bool HasOpenTurn(IReadOnlyList<Harness.Session.SessionEvent> events)
    {
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index] is TurnStartEvent) return true;
            if (events[index] is TurnEndEvent) return false;
        }
        return false;
    }

    private async Task<ApprovalOutcome> DecideAsync(ApprovalRequest request, Harness.Session.Session session)
    {
        if (request.CancellationToken is { IsCancellationRequested: true }) return ApprovalOutcome.Cancelled;
        // The 'never' policy is decided HERE, before any dispatch: a listener registered after
        // this service mounts would sit ahead of any gate LISTENER, so a listener-shaped gate
        // cannot keep the promise that 'never' rejects deterministically regardless of order.
        if (EffectivePolicy(session) == ApprovalPolicy.Never) return ApprovalOutcome.Rejected;
        var answer = DispatchAsync(request);
        if (request.CancellationToken is not { } token) return await NormalizeAsync(answer);
        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => tcs.TrySetResult(ApprovalOutcome.Cancelled));
        _ = NormalizeAsync(answer).ContinueWith(completed => tcs.TrySetResult(completed.Result), TaskScheduler.Default);
        return await tcs.Task;
    }

    /// <summary>Dispatch the waterfall, contained: a listener that throws synchronously must land in the same path as an async one.</summary>
    private Task<ApprovalOutcome> DispatchAsync(ApprovalRequest request)
    {
        try
        {
            return Ctx.Waterfall<Task<ApprovalOutcome>>(RequestEvent, new object?[] { request },
                () => Task.FromResult(ApprovalOutcome.Unavailable));
        }
        catch (Exception)
        {
            return Task.FromResult(ApprovalOutcome.Unavailable);
        }
    }

    /// <summary>Normalize a rogue (non-vocabulary) answerer return to the fail-closed outcome.</summary>
    private static async Task<ApprovalOutcome> NormalizeAsync(Task<ApprovalOutcome> answer)
    {
        ApprovalOutcome outcome;
        try
        {
            outcome = await answer;
        }
        catch (Exception)
        {
            return ApprovalOutcome.Unavailable;
        }
        return Outcomes.Contains(outcome) ? outcome : ApprovalOutcome.Unavailable;
    }
}
