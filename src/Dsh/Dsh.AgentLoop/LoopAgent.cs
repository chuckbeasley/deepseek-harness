namespace Dsh.AgentLoop;

/// <summary>
/// The default agent driver over queued turns and step-boundary input (port of the TS
/// ReactLoopAgent). Every request is derived from the session log. One instance drives one
/// <see cref="Dsh.Agent.Agent"/>: status, step boundaries, and inbox claims go through the
/// agent's own API, while the loop owns the phase machine, the per-activity abort signal, and
/// the quiescence promise. Disposal quiescence: the activity token links the agent's lifecycle
/// signal, so the registry's teardown (or a handle disposal) aborts the running turn, whose
/// aborted reason is then logged before the driver exits.
/// </summary>
public sealed class LoopAgent
{
    private readonly object _gate = new();
    private readonly Dsh.Agent.Agent _agent;
    private readonly LoopRuntime _runtime;
    private readonly RuntimeContextProjection _runtimeContext;
    private readonly IReadOnlyList<Func<Task<RuntimeContextPart>>> _contextProviders;

    private CancellationTokenSource? _activityCancel;
    private CancellationTokenSource? _activity;
    private Task _activityDone = Task.CompletedTask;
    private bool _running;
    private bool _wakeRequested;
    private long _lastTurn;
    private TurnEndCancelCause? _cancelCause;
    private bool _requestHeaderLogged;

    /// <summary>
    /// Create the driver for <paramref name="agent"/>.
    /// </summary>
    /// <param name="ownerCtx">the agent's owner context; the runtime-context projection observes its <c>session/event</c> stream.</param>
    /// <param name="agent">the live agent to drive.</param>
    /// <param name="runtime">the resolved service dependencies.</param>
    /// <param name="contextProviders">dynamic runtime-context contributions evaluated per pre-step (empty by default).</param>
    public LoopAgent(Context ownerCtx, Dsh.Agent.Agent agent, LoopRuntime runtime, IReadOnlyList<Func<Task<RuntimeContextPart>>>? contextProviders = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _contextProviders = contextProviders ?? Array.Empty<Func<Task<RuntimeContextPart>>>();
        _lastTurn = agent.Session.Events.OfType<TurnStartEvent>().Select(evt => evt.Turn).DefaultIfEmpty(0).Max();
        _runtimeContext = new RuntimeContextProjection(ownerCtx, agent.Session);
    }

    /// <summary>The driven agent.</summary>
    public Dsh.Agent.Agent Agent => _agent;

    /// <summary>Whether a driver is active.</summary>
    public bool IsRunning { get { lock (_gate) return _running; } }

    /// <summary>The quiescence promise: completes when the current activity settles.</summary>
    public Task WhenIdleAsync() => _activityDone;

    /// <summary>
    /// Insert one message and optionally wake the driver. A waking message sent while the active
    /// activity is already aborted cannot join it, so it is reclassified to the next turn.
    /// </summary>
    public void Send(UserMessage message, InboxTarget target, bool wakeup)
    {
        ArgumentNullException.ThrowIfNull(message);
        bool wakeAfterAbort;
        lock (_gate)
        {
            wakeAfterAbort = wakeup && _running && _activityCancel is { IsCancellationRequested: true };
        }
        _agent.Inbox.Append(wakeAfterAbort ? InboxTarget.NextTurn : target, message);
        if (wakeup) WakeDriver();
    }

    /// <summary>Queue one message as a new turn and wake the driver.</summary>
    public void Followup(UserMessage message) => Send(message, InboxTarget.NextTurn, wakeup: true);

    /// <summary>Queue one message for the current turn's next step and wake the driver.</summary>
    public void Steer(UserMessage message) => Send(message, InboxTarget.NextStep, wakeup: true);

    /// <summary>Queue one message for the current turn's next step without waking the driver.</summary>
    public void Inject(UserMessage message) => Send(message, InboxTarget.NextStep, wakeup: false);

    /// <summary>
    /// Abort the active activity. The first cause wins; the inbox is cleared unless
    /// <paramref name="keepInbox"/> is set. With no active activity, cancellation only clears the
    /// inbox (lifecycle cancellation remains the agent's own API).
    /// </summary>
    public void Cancel(TurnEndCancelCause? cause = null, bool keepInbox = false)
    {
        if (!keepInbox)
        {
            _agent.Inbox.Clear();
        }
        lock (_gate)
        {
            _cancelCause ??= cause ?? new UserCancel();
            if (!_running) return;
            _activityCancel?.Cancel();
        }
    }

    /// <summary>
    /// Start one driver when idle, or latch the wake for the running activity's boundary. The
    /// live driver claims queued work itself at each turn boundary, so a latched wake that was
    /// consumed is a no-op.
    /// </summary>
    public void WakeDriver()
    {
        lock (_gate)
        {
            if (_running)
            {
                _wakeRequested = true;
                return;
            }
            _wakeRequested = false;
            _running = true;
            _cancelCause = null;
            _activityCancel = new CancellationTokenSource();
            _activity = CancellationTokenSource.CreateLinkedTokenSource(_agent.CancellationToken, _activityCancel.Token);
            var activity = _activity;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activityDone = done.Task;
            _agent.SetStatus(AgentStatus.Running);
            _ = KickAsync(activity, done);
        }
    }

    private async Task KickAsync(CancellationTokenSource activity, TaskCompletionSource done)
    {
        try
        {
            while (await RunTurnAsync(activity.Token)) { }
        }
        catch (OperationCanceledException) when (activity.IsCancellationRequested)
        {
            // Cancellation is contained at the driver boundary; the turn logged its aborted reason.
        }
        catch (Exception)
        {
            // Reported failures are contained at the driver boundary (agent/error).
        }
        finally
        {
            bool replay;
            lock (_gate)
            {
                activity.Dispose();
                if (ReferenceEquals(_activity, activity)) _activity = null;
                _activityCancel?.Dispose();
                _activityCancel = null;
                _running = false;
                replay = _wakeRequested && _agent.Inbox.HasPending;
                _wakeRequested = false;
            }
            _agent.SetStatus(AgentStatus.Idle);
            done.SetResult();
            if (replay) WakeDriver();
        }
    }

    /// <summary>Open one turn before claiming its first proposed step; returns whether another turn should run.</summary>
    private async Task<bool> RunTurnAsync(CancellationToken ct)
    {
        var turn = _lastTurn + 1;
        _agent.Session.Append(new TurnStartEvent { Turn = turn });
        _lastTurn = turn;
        TurnEndReason? turnEnds = null;
        var target = InboxTarget.NextTurn;
        long stepCounter = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                stepCounter += 1;
                var decision = await PreStepAsync(target, turn, stepCounter, ct);
                if (decision is RejectDecision)
                {
                    turnEnds = new BlockedReason();
                    return false;
                }
                var entered = (EnterDecision)decision;
                if (turnEnds is not null && entered.Messages.Count == 0) break;
                // A removed waking message or an enter decision rewritten to empty still owns the
                // initial turn boundary, but it spends no model call.
                if (stepCounter == 1 && entered.Messages.Count == 0)
                {
                    turnEnds = new CompletedReason();
                    return false;
                }
                ct.ThrowIfCancellationRequested();
                _agent.Session.Append(new StepStartEvent { Turn = turn, Step = stepCounter });
                _agent.StartStep(turn, stepCounter);
                try
                {
                    foreach (var message in entered.Messages)
                    {
                        _agent.Session.Append(new UserMessageEvent { Message = message, SurfaceOp = SurfaceOp.Append });
                    }
                    var stepEnd = await StepAsync(turn, stepCounter, entered.Assembly, startsRequestSeries: stepCounter == 1, ct);
                    // max-tokens is sticky: a later completed step must not downgrade the turn outcome.
                    if (turnEnds is null || turnEnds is not MaxTokensReason) turnEnds = stepEnd;
                }
                finally
                {
                    _agent.Session.Append(new StepEndEvent { Turn = turn, Step = stepCounter });
                    _agent.EndStep();
                }
                ct.ThrowIfCancellationRequested();
                if (turnEnds is not null && _agent.Inbox.NextStep.Count == 0)
                {
                    EmitContained(LoopEvents.TurnStopping, new TurnStoppingProposal(_agent, turn));
                }
                ct.ThrowIfCancellationRequested();
                if (turnEnds is not null && _agent.Inbox.NextStep.Count == 0) break;
                target = InboxTarget.NextStep;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            turnEnds = new AbortedReason(CancelCause());
            throw;
        }
        catch (Exception error)
        {
            // Every failure is structured: an LlmError keeps its facts, anything else flattens.
            turnEnds = new ErrorReason(error is LlmError llm ? llm.Failure : new LlmFailure(error.Message, "UNKNOWN"));
            EmitContained(LoopEvents.Error, new AgentErrorPayload(_agent, turn, _agent.Step, error));
            throw;
        }
        finally
        {
            try
            {
                _agent.Session.Append(new TurnEndEvent { Turn = turn, Reason = turnEnds ?? new CompletedReason() });
            }
            catch (Exception appendError)
            {
                throw new InvalidOperationException($"agent \"{_agent.Id}\": turn/end append failed: {appendError.Message}", appendError);
            }
        }
        return _agent.Inbox.HasPending;
    }

    /// <summary>Claim the step's input, assemble the system prompt, and propose the step to listeners.</summary>
    private async Task<PreStepDecision> PreStepAsync(InboxTarget target, long turn, long step, CancellationToken ct)
    {
        var claimed = _agent.Inbox.Claim(target, turn);
        var assembly = await _runtime.SystemPrompt.AssembleAsync();
        var parts = new List<RuntimeContextPart>();
        foreach (var provider in _contextProviders)
        {
            parts.Add(await provider());
        }
        var current = string.Join("\n\n", parts.Select(part => part.Text).Where(text => text.Length > 0));
        var sections = parts.SelectMany(part => part.Sections).ToArray();
        var context = _runtimeContext.Project(current, sections);
        var messages = context is null ? claimed.ToList() : claimed.Concat(new[] { context }).ToList();
        var proposal = new PreStepProposal(_agent, messages, turn, step);
        var decision = await _agent.Owner.Waterfall<Task<PreStepDecision>>(
            LoopEvents.PreStep,
            new object?[] { proposal },
            () => Task.FromResult<PreStepDecision>(new EnterDecision(messages, assembly)));
        ct.ThrowIfCancellationRequested();
        return decision;
    }

    /// <summary>Run one step: one model request plus the tools it calls, until no tool call remains.</summary>
    private async Task<TurnEndReason?> StepAsync(long turn, long step, PromptAssembly? assembly, bool startsRequestSeries, CancellationToken ct)
    {
        var system = assembly is null ? string.Empty : _runtime.SystemPrompt.RenderPrompt(assembly);
        var tools = assembly?.Tools ?? Array.Empty<ToolSchema>();
        while (true)
        {
            var (request, _) = await BuildRequestAsync(turn, step, system, tools, startsRequestSeries, ct);
            startsRequestSeries = false;
            var assembler = new BlockAssembler();
            var chunkSeqs = new List<long>();
            try
            {
                await foreach (var chunk in _runtime.Llm.Stream(request, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    chunkSeqs.Add(_agent.Session.Append(new AssistantChunkEvent { Turn = turn, Step = step, Chunk = chunk }).Seq);
                    assembler.Push(chunk);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // A cancelled stream finalizes the safe prefix so replay stays valid.
                var interrupted = assembler.InterruptedBlocks();
                if (interrupted.Count > 0)
                {
                    _agent.Session.Append(new AssistantMessageEvent
                    {
                        Turn = turn, Step = step,
                        Message = Messages.CreateAssistantMessage(request.Provider, request.Model, interrupted),
                        Interrupted = true,
                        Usage = assembler.Usage,
                        SurfaceOp = SurfaceOp.Append,
                        SourceEventSeqs = chunkSeqs,
                    });
                }
                throw;
            }
            var finish = assembler.Finish;
            var streamFailure = finish switch
            {
                Dsh.Llm.Error error => error.Failure,
                Dsh.Llm.Aborted aborted => aborted.Failure,
                _ => null,
            };
            if (streamFailure is not null)
            {
                var proposal = new RequestErrorProposal(_agent, turn, step, request.Provider, streamFailure);
                var action = await _agent.Owner.Waterfall<Task<RequestErrorAction?>>(
                    LoopEvents.RequestError,
                    new object?[] { proposal },
                    () => Task.FromResult<RequestErrorAction?>(null));
                ct.ThrowIfCancellationRequested();
                if (action is not RetryDecision)
                {
                    throw new LlmError(streamFailure.Message, streamFailure.Code, streamFailure.Status);
                }
                continue;
            }
            var blocks = assembler.Blocks();
            var assistant = Messages.CreateAssistantMessage(request.Provider, request.Model, blocks);
            _agent.Session.Append(new AssistantMessageEvent
            {
                Turn = turn, Step = step,
                Message = assistant,
                Usage = assembler.Usage,
                SurfaceOp = SurfaceOp.Append,
                SourceEventSeqs = chunkSeqs,
            });
            if (finish is MaxTokens) return new MaxTokensReason();
            var toolCalls = blocks.OfType<ToolCallBlock>().ToArray();
            if (toolCalls.Length == 0) return new CompletedReason();
            var concluded = await ToolCallScheduler.ExecuteAsync(_agent, _runtime.Tools, turn, step, toolCalls, ct);
            return concluded ? new CompletedReason() : null;
        }
    }

    /// <summary>Compose one request from the folded header and bind it to the durable request events.</summary>
    private async Task<(GenerateOptions Request, EpochHeader Header)> BuildRequestAsync(
        long turn, long step, string system, IReadOnlyList<ToolSchema> tools, bool startsRequestSeries, CancellationToken ct)
    {
        var session = _agent.Session;
        var persistedHeader = LastHeader();
        // A loop instance starts from its declared route; later steps re-resolve marked defaults.
        var route = new LlmCallConfig(
            _agent.Options.Provider ?? string.Empty,
            _agent.Options.Model ?? string.Empty,
            MaxTokens: _agent.Options.MaxTokens);
        var seed = _requestHeaderLogged && persistedHeader is not null
            ? RequestProposal(persistedHeader)
            : route;
        var proposal = new RequestProposal(_agent, turn, step, seed);
        var config = await _agent.Owner.Waterfall<Task<LlmCallConfig>>(
            LoopEvents.Request,
            new object?[] { proposal },
            () => Task.FromResult(seed));
        ct.ThrowIfCancellationRequested();
        if (config.Provider.Length == 0 || config.Model.Length == 0)
        {
            throw new InvalidOperationException(
                $"agent \"{_agent.Id}\" has no provider/model: set AgentOptions.Provider and AgentOptions.Model or supply both via the agent/request waterfall");
        }
        // Exact-model adapter defaults: the proposal may omit maxTokens/reasoningEffort and the
        // adapter's model metadata materializes them (port of the TS prepareCall resolution). The
        // resolved config is what the header and the dispatch carry.
        var metadata = _runtime.Llm.ResolveModelMetadata(config.Provider, config.Model);
        var resolved = ResolveCallConfig(config, metadata);
        var header = new EpochHeader
        {
            Config = resolved.Config,
            AdapterDefaults = resolved.Defaults is { ReasoningEffort: false, MaxTokens: false } ? null : resolved.Defaults,
            System = system.Length == 0 ? null : system,
            Tools = tools.Count > 0 ? tools : null,
        };
        var baseline = LastHeader();
        if (!_requestHeaderLogged)
        {
            session.Append(new RequestHeaderEvent
            {
                Header = header,
                Reason = baseline is null ? RequestHeaderReason.Initial : RequestHeaderReason.Resume,
            });
            _requestHeaderLogged = true;
        }
        else if (baseline is null || !HeadersEqual(baseline, header))
        {
            session.Append(new RequestHeaderEvent
            {
                Header = header,
                Reason = RequestHeaderReason.Change,
                StartsSeries = startsRequestSeries,
            });
        }
        else if (startsRequestSeries)
        {
            session.Append(new RequestHeaderEvent { Header = header, Reason = RequestHeaderReason.Series });
        }
        var requestContext = new RequestContextEvent
        {
            Provider = resolved.Config.Provider,
            Model = resolved.Config.Model,
            ContextWindow = metadata?.ContextWindow,
        };
        var previous = LastContext();
        if (previous is null
            || previous.Provider != requestContext.Provider
            || previous.Model != requestContext.Model
            || previous.ContextWindow != requestContext.ContextWindow)
        {
            session.Append(requestContext);
        }
        ct.ThrowIfCancellationRequested();
        var request = new GenerateOptions(
            resolved.Config.Provider, resolved.Config.Model, session.DeriveMessages(),
            System: header.System, Tools: header.Tools,
            Temperature: resolved.Config.Temperature, MaxTokens: resolved.Config.MaxTokens,
            CancellationToken: ct)
        {
            SessionId = session.Id.Value,
        };
        return (request, header);
    }

    /// <summary>
    /// Apply exact-model adapter defaults to a proposed config (port of the TS
    /// <c>resolveCallWithInfo</c>): a missing maxTokens takes the model's default; a missing
    /// reasoning effort takes the model's default effort when the model supports reasoning.
    /// Adapter-defaulted fields are flagged so later proposals can strip them again.
    /// </summary>
    private static (LlmCallConfig Config, LlmCallConfigAdapterDefaults Defaults) ResolveCallConfig(
        LlmCallConfig config, LlmModelMetadata? info)
    {
        if (info is null) return (config, new LlmCallConfigAdapterDefaults());
        var defaulted = config.MaxTokens is null && info.DefaultMaxTokens is not null
            ? config with { MaxTokens = info.DefaultMaxTokens }
            : config;
        var requested = defaulted.ReasoningEffort;
        var resolved = defaulted;
        if (info.DefaultReasoningEffort is null && info.ReasoningEfforts is null)
        {
            if (requested is not null)
            {
                throw new LlmError(
                    $"provider \"{config.Provider}\" model \"{config.Model}\" does not support reasoning effort \"{requested.Value}\"",
                    "UNSUPPORTED_REASONING_EFFORT");
            }
        }
        else
        {
            var effective = requested ?? info.DefaultReasoningEffort;
            if (effective is not null)
            {
                if (info.ReasoningEfforts is not null && !info.ReasoningEfforts.Contains(effective))
                {
                    throw new LlmError(
                        $"provider \"{config.Provider}\" model \"{config.Model}\" does not support reasoning effort \"{effective}\"",
                        "UNSUPPORTED_REASONING_EFFORT");
                }
                if (requested != effective) resolved = defaulted with { ReasoningEffort = new ReasoningEffortId(effective) };
            }
        }
        var defaults = new LlmCallConfigAdapterDefaults(
            ReasoningEffort: config.ReasoningEffort is null && resolved.ReasoningEffort is not null,
            MaxTokens: config.MaxTokens is null && resolved.MaxTokens is not null);
        return (resolved, defaults);
    }

    /// <summary>The last durable request header snapshot, or null when the log has none.</summary>
    private EpochHeader? LastHeader()
        => _agent.Session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header).LastOrDefault();

    /// <summary>The last durable request-context snapshot, or null when the log has none.</summary>
    private RequestContextEvent? LastContext()
        => _agent.Session.Events.OfType<RequestContextEvent>().LastOrDefault();

    /// <summary>Remove adapter-derived values before plugins propose the next request config.</summary>
    private static LlmCallConfig RequestProposal(EpochHeader header)
    {
        var proposal = header.Config with { };
        if (header.AdapterDefaults?.ReasoningEffort == true) proposal = proposal with { ReasoningEffort = null };
        if (header.AdapterDefaults?.MaxTokens == true) proposal = proposal with { MaxTokens = null };
        return proposal;
    }

    /// <summary>Field-wise equality over the request-visible header fields.</summary>
    private static bool HeadersEqual(EpochHeader a, EpochHeader b)
    {
        if (!CallConfig.Equals(a.Config, b.Config)) return false;
        if (a.System != b.System) return false;
        if (a.Tools is null || b.Tools is null) return a.Tools is null && b.Tools is null;
        if (a.Tools.Count != b.Tools.Count) return false;
        for (var index = 0; index < a.Tools.Count; index++)
        {
            if (a.Tools[index].Name != b.Tools[index].Name
                || a.Tools[index].Description != b.Tools[index].Description
                || a.Tools[index].Parameters.GetRawText() != b.Tools[index].Parameters.GetRawText())
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The cause of the active cancellation (loop-owned, agent-recorded, or disposed).</summary>
    private TurnEndCancelCause CancelCause()
    {
        lock (_gate)
        {
            return _cancelCause ?? _agent.LastCancelCause ?? new DisposedCancel();
        }
    }

    /// <summary>Emit one loop event through the owner context, containing listener failures.</summary>
    private void EmitContained(string name, object payload)
    {
        try
        {
            _agent.Owner.Emit(name, payload);
        }
        catch (Exception error)
        {
            _agent.Owner.Logger.Warn($"agent \"{_agent.Id}\": {name} listener threw: {error.Message}");
        }
    }
}
