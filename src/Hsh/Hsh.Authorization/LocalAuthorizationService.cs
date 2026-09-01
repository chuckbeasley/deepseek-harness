using Harness.Cordis.Core;
using Harness.Credentials;

namespace Harness.Authorization;

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

/// <summary>One attempt in flight, with the handle that withdraws it.</summary>
internal sealed class InFlight
{
    public required CancellationTokenSource Controller { get; init; }
}

/// <summary>Mutable observation flags one attempt collects while its flow runs.</summary>
internal sealed class ObservedState
{
    public bool Declined;

    public bool Committed;
}

/// <summary>
/// Write-observing facade over the credentials service that one attempt hands its flow. The
/// commit the seam confirms is a credential-record write, so the store is required, not optional:
/// reads and describes delegate to the real service unchanged, and a successful write to the
/// flow's own key reports the observation the seam must collect.
/// </summary>
internal sealed class CommitObservingCredentials : ICredentialsService
{
    private readonly ICredentialsService _inner;
    private readonly string _key;
    private readonly Action _onWrite;

    public CommitObservingCredentials(ICredentialsService inner, string key, Action onWrite)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _onWrite = onWrite ?? throw new ArgumentNullException(nameof(onWrite));
    }

    public Task<ResolvedCredential?> ResolveAsync(string reference, CancellationToken cancellationToken = default)
        => _inner.ResolveAsync(reference, cancellationToken);

    public Task<ResolvedCredential> RequireAsync(string reference, CancellationToken cancellationToken = default)
        => _inner.RequireAsync(reference, cancellationToken);

    public Task<CredentialInfo> DescribeAsync(string reference, CancellationToken cancellationToken = default)
        => _inner.DescribeAsync(reference, cancellationToken);

    public async Task SetAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        await _inner.SetAsync(reference, value, cancellationToken).ConfigureAwait(false);
        if (string.Equals(reference, _key, StringComparison.Ordinal)) _onWrite();
    }

    public async Task UnsetAsync(string reference, CancellationToken cancellationToken = default)
    {
        await _inner.UnsetAsync(reference, cancellationToken).ConfigureAwait(false);
        if (string.Equals(reference, _key, StringComparison.Ordinal)) _onWrite();
    }
}

/// <summary>
/// The authorization service (ctx.authorization): a registry of credential-obtaining flows, one
/// attempt at a time per key. Port of <c>@deepseek-ai/hsh-authorization</c>.
///
/// Deviation from the TS seam: the C# credentials port has not landed the
/// <c>credentials/record-updated</c> event yet, so the commit this seam confirms is observed
/// through a write-observing facade of <see cref="ICredentialsService"/> that one attempt hands
/// its flow via <see cref="AuthorizationSession.Credentials"/>, instead of a bus-wide event; the
/// final presence check still reads the real credentials service.
/// </summary>
public sealed class LocalAuthorizationService : Service, IAuthorizationService
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "authorization";

    private readonly ICredentialsService _credentials;
    private readonly Dictionary<string, AuthorizationFlow> _flows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InFlight> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// Create and register the authorization service under the <c>authorization</c> key over the
    /// mounted <paramref name="credentials"/> service.
    /// </summary>
    public LocalAuthorizationService(Context ctx, ICredentialsService credentials)
        : base(ctx, ServiceKey)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    /// <inheritdoc />
    public IDisposable RegisterFlow(AuthorizationFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Methods is null || flow.Methods.Count == 0)
        {
            throw new ArgumentException("an authorization flow must offer at least one method", nameof(flow));
        }
        return Ctx.Effect(() =>
        {
            if (_flows.ContainsKey(flow.Key))
            {
                throw new AuthorizationError(
                    $"an authorization flow for \"{flow.Key}\" is already registered", "DUPLICATE_FLOW");
            }
            _flows[flow.Key] = flow;
            return new ActionDisposer(() =>
            {
                _flows.Remove(flow.Key);
                // A flow leaving mid-attempt takes its attempt with it: the runner belongs to a
                // plugin that is going away, so letting it keep prompting would outlive the
                // surface that can answer for it.
                if (_running.TryGetValue(flow.Key, out var inFlight)) inFlight.Controller.Cancel();
            });
        }, $"authorization.registerFlow(\"{flow.Key}\")");
    }

    /// <inheritdoc />
    public IReadOnlyList<AuthorizationEntry> List() => _flows.Values.Select(Entry).ToList();

    /// <inheritdoc />
    public AuthorizationEntry? Describe(string key)
        => _flows.TryGetValue(key, out var flow) ? Entry(flow) : null;

    /// <summary>The public view of one registered flow.</summary>
    private AuthorizationEntry Entry(AuthorizationFlow flow)
        => new(flow.Key, flow.Label, flow.Methods, _running.ContainsKey(flow.Key));

    /// <inheritdoc />
    public void Cancel(string key)
    {
        if (_running.TryGetValue(key, out var inFlight)) inFlight.Controller.Cancel();
    }

    /// <inheritdoc />
    public async Task<AuthorizationOutcome> BeginAsync(AuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Key;
        if (!_flows.TryGetValue(key, out var flow))
        {
            throw new AuthorizationError($"no authorization flow is registered for \"{key}\"", "NO_FLOW");
        }
        var method = request.Method ?? flow.Methods[0].Id;
        if (!flow.Methods.Any(candidate => candidate.Id == method))
        {
            throw new AuthorizationError(
                $"authorization flow for \"{key}\" offers no method \"{method}\"", "UNKNOWN_METHOD");
        }
        if (_running.ContainsKey(key))
        {
            throw new AuthorizationError(
                $"an authorization attempt for \"{key}\" is already running", "ALREADY_IN_FLIGHT");
        }
        // Withdrawn before it began: never claim the slot and never run the flow. Handing a
        // cancelled signal to the run would rely on every flow checking it before its first
        // await, and one that does not would hang holding the key. Validation still runs first,
        // so a caller naming a key or method that does not exist hears about it whether or not it
        // also gave up.
        if (request.Signal.IsCancellationRequested)
        {
            return new AuthorizationOutcome(AuthorizationStatus.Cancelled);
        }
        var controller = new CancellationTokenSource();
        using var withdrawal = request.Signal.Register(() => controller.Cancel());
        _running[key] = new InFlight { Controller = controller };
        var settlement = AuthorizationSettlement.Failed;
        try
        {
            var outcome = await AttemptAsync(flow, method, controller.Token, request.Interaction).ConfigureAwait(false);
            settlement = outcome.Status == AuthorizationStatus.Authorized
                ? AuthorizationSettlement.Authorized
                : AuthorizationSettlement.Cancelled;
            return outcome;
        }
        finally
        {
            _running.Remove(key);
            // After the slot is released, so a listener that reacts by starting the next attempt
            // is not refused by the one that just finished.
            Settle(key, settlement);
        }
    }

    /// <summary>Run the flow, then hold it to its half of the commit contract.</summary>
    private async Task<AuthorizationOutcome> AttemptAsync(
        AuthorizationFlow flow,
        string method,
        CancellationToken signal,
        AuthorizationInteraction interaction)
    {
        // Withdrawal settles the attempt whether or not the flow reacts to it. A flow is supposed
        // to stop when its signal fires, but one that does not would otherwise hold the key for
        // the life of the process, and a wedged key is indistinguishable from a busy one from the
        // outside. The orphaned run is left to finish on its own; nothing waits on it, and a
        // record it still manages to commit is a record the human did authorize.
        var withdrawn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var withdrawal = signal.Register(() => withdrawn.TrySetResult(true));
        // What the seam itself witnessed during the run, held as a mutable object because closure
        // writes to a captured local do not narrow across awaits: the prompt wrapper sees a
        // decline first-hand (a flow that rewraps the rejection on its way out cannot hide it),
        // and confirming the commit means confirming it happened *now* — on a re-auth the record
        // already exists, so presence alone would let a flow that wrote nothing report the stale
        // credential as freshly authorized.
        var observed = new ObservedState();
        var session = new AuthorizationSession
        {
            Method = method,
            Signal = signal,
            Notify = notice => NotifyInteraction(interaction, notice),
            PromptAsync = prompt => PromptInteraction(interaction, prompt, observed),
            Credentials = new CommitObservingCredentials(_credentials, flow.Key, () => observed.Committed = true),
        };
        Task running;
        try
        {
            running = flow.Run(session);
        }
        catch (Exception) when (signal.IsCancellationRequested || observed.Declined)
        {
            // A withdrawn attempt and a declined prompt are outcomes, not failures: the human
            // said no, or closed the page. Anything else is the flow failing and belongs to the
            // caller, cause chain intact.
            return new AuthorizationOutcome(AuthorizationStatus.Cancelled);
        }
        var completed = await Task.WhenAny(running, withdrawn.Task).ConfigureAwait(false);
        if (ReferenceEquals(completed, withdrawn.Task))
        {
            // Nothing awaits the orphan any more, so its eventual failure has to be marked
            // handled or it would surface as an unobserved task exception.
            _ = ObserveOrphanAsync(running);
            return new AuthorizationOutcome(AuthorizationStatus.Cancelled);
        }
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception) when (signal.IsCancellationRequested || observed.Declined)
        {
            return new AuthorizationOutcome(AuthorizationStatus.Cancelled);
        }
        if (!observed.Committed)
        {
            throw new AuthorizationError(
                $"authorization flow for \"{flow.Key}\" resolved without committing a credential record in this attempt",
                "NOT_COMMITTED");
        }
        var stored = await _credentials.DescribeAsync(flow.Key).ConfigureAwait(false);
        if (!stored.Configured)
        {
            throw new AuthorizationError(
                $"authorization flow for \"{flow.Key}\" deleted its credential record instead of committing one",
                "NOT_COMMITTED");
        }
        return new AuthorizationOutcome(AuthorizationStatus.Authorized);
    }

    /// <summary>Route a flow's notice to the interaction surface; a broken surface loses the notice, never the attempt.</summary>
    private void NotifyInteraction(AuthorizationInteraction interaction, AuthorizationNotice notice)
    {
        try
        {
            interaction.Notify(notice);
        }
        catch (Exception error)
        {
            // Fire-and-forget is held at the seam: a surface that cannot render a notice (a page
            // whose connection just closed) loses the notice, never the attempt.
            Ctx.Logger.Warn($"authorization: the interaction surface failed to render a notice: {error.Message}");
        }
    }

    /// <summary>Route a flow's prompt to the interaction surface, marking the decline the seam must read first-hand.</summary>
    private static async Task<string> PromptInteraction(
        AuthorizationInteraction interaction, AuthorizationPrompt prompt, ObservedState observed)
    {
        try
        {
            return await interaction.PromptAsync(prompt).ConfigureAwait(false);
        }
        catch (AuthorizationDeclinedError)
        {
            observed.Declined = true;
            throw;
        }
    }

    /// <summary>Observe a withdrawn flow's eventual outcome so its late failure stays handled.</summary>
    private async Task ObserveOrphanAsync(Task running)
    {
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception)
        {
            Ctx.Logger.Debug("authorization: a withdrawn flow failed after the fact");
        }
    }

    /// <summary>Fan <c>authorization/settled</c> out with contained listener failures.</summary>
    private void Settle(string key, AuthorizationSettlement settlement)
    {
        try
        {
            Ctx.Emit("authorization/settled", key, settlement);
        }
        catch (Exception error)
        {
            // The attempt is already over and its key released when this fires, so a broken
            // watcher (that second browser tab) can never turn the caller's settled result into a
            // failure of its own.
            Ctx.Logger.Warn($"authorization: an authorization/settled listener failed: {error.Message}");
        }
    }
}
