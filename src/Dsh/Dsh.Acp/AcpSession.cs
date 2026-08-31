using System.Text.Json;
using Cordis.Core;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Llm;
using Dsh.Sdk.Protocol;
using Dsh.Session;

namespace Dsh.Acp;

/// <summary>One in-flight ACP prompt's correlation and settlement state.</summary>
internal sealed class InflightPrompt
{
    /// <summary>The prompt's stop-reason settlement.</summary>
    public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The queued user message id (set at admission).</summary>
    public string? MessageId { get; set; }

    /// <summary>Whether the user message was enqueued on the agent.</summary>
    public bool MessageQueued { get; set; }

    /// <summary>The Agent turn allocated to the queued message (set by the inbox-claimed edge).</summary>
    public long? Turn { get; set; }

    /// <summary>The correlated turn's durable end reason.</summary>
    public TurnEndReason? EndReason { get; set; }

    /// <summary>Whether an explicit cancel settled this prompt.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>Whether quiescent settlement already started.</summary>
    public bool SettlementStarted { get; set; }

    /// <summary>The first assistant-output delivery failure, if any.</summary>
    public Exception? OutputError { get; set; }

    /// <summary>The first out-of-turn Agent interval failure, if any.</summary>
    public Exception? AgentError { get; set; }
}

/// <summary>
/// One standard ACP session's Agent, prompt admission, ordered standard updates, and quiescent
/// teardown (port of the TS <c>AcpSession</c>). The ported loop reads AgentOptions at agent
/// creation, so the session's model selection is fixed for its lifetime (documented reduction).
/// </summary>
public sealed class AcpSession
{
    private readonly Context _ctx;
    private readonly AgentHandle _handle;
    private readonly LoopAgent _driver;
    private readonly AcpModelControl _modelControl;
    private readonly Func<SessionUpdate, Task> _notify;
    private readonly object _gate = new();
    private Task _outputTail = Task.CompletedTask;
    private InflightPrompt? _inflight;
    private Task? _closing;

    /// <summary>Create the session module over one published agent and its loop driver.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="handle">the published agent handle.</param>
    /// <param name="driver">the agent's loop driver.</param>
    /// <param name="modelControl">the session's model configuration state.</param>
    /// <param name="notify">delivers one ordered standard update to the client.</param>
    public AcpSession(Context ctx, AgentHandle handle, LoopAgent driver, AcpModelControl modelControl, Func<SessionUpdate, Task> notify)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _modelControl = modelControl ?? throw new ArgumentNullException(nameof(modelControl));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    /// <summary>The exact top-level Agent owned by this ACP session.</summary>
    public Dsh.Agent.Agent Agent => _handle.Agent;

    /// <summary>The exact Session owned by this ACP session.</summary>
    public Dsh.Session.Session Session => _handle.Agent.Session;

    /// <summary>Compose a fresh Agent and driver on the ported loop.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="loop">the agent loop.</param>
    /// <param name="sessionId">the fresh session identity.</param>
    /// <param name="options">the route options, or <c>null</c> for the loop defaults.</param>
    /// <param name="modelControl">the session's model configuration state.</param>
    /// <param name="notify">delivers one ordered standard update to the client.</param>
    /// <returns>the session module.</returns>
    public static AcpSession Create(Context ctx, Dsh.AgentLoop.AgentLoop loop, string sessionId, AgentOptions? options,
        AcpModelControl modelControl, Func<SessionUpdate, Task> notify)
    {
        var id = new SessionId(sessionId);
        var handle = loop.Create(id, options);
        var driver = loop.GetLoop(id)
            ?? throw new InvalidOperationException("acp: the loop published no driver");
        return new AcpSession(ctx, handle, driver, modelControl, notify);
    }

    /// <summary>Restore a persisted Agent and its driver on the ported loop.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="loop">the agent loop.</param>
    /// <param name="sessionId">the persisted session identity.</param>
    /// <param name="options">the route options, or <c>null</c> for the loop defaults.</param>
    /// <param name="modelControl">the session's model configuration state.</param>
    /// <param name="notify">delivers one ordered standard update to the client.</param>
    /// <returns>the session module.</returns>
    public static AcpSession Resume(Context ctx, Dsh.AgentLoop.AgentLoop loop, string sessionId, AgentOptions? options,
        AcpModelControl modelControl, Func<SessionUpdate, Task> notify)
    {
        var id = new SessionId(sessionId);
        var handle = loop.Resume(id, options);
        var driver = loop.GetLoop(id)
            ?? throw new InvalidOperationException("acp: the loop published no driver after resume");
        return new AcpSession(ctx, handle, driver, modelControl, notify);
    }

    /// <summary>Whether this module owns an exact Agent reference.</summary>
    /// <param name="agent">the Agent observed on a scoped runtime event.</param>
    /// <returns><c>true</c> only for this session's owned Agent.</returns>
    public bool Owns(Dsh.Agent.Agent agent) => ReferenceEquals(Agent, agent);

    /// <summary>Whether this module owns an exact Session reference.</summary>
    /// <param name="session">the Session observed on a durable event.</param>
    /// <returns><c>true</c> only for this session's owned Session.</returns>
    public bool OwnsSession(Dsh.Session.Session session) => ReferenceEquals(Session, session);

    /// <summary>Return the complete standard model configuration state.</summary>
    /// <returns>the provider-grouped model option.</returns>
    public IReadOnlyList<SessionConfigOption> ConfigOptions()
    {
        AssertActive();
        return _modelControl.Options();
    }

    /// <summary>Apply one standard configuration option to later ACP turns.</summary>
    /// <param name="configId">the advertised standard option id.</param>
    /// <param name="value">the opaque selected value.</param>
    /// <returns>the complete resulting option state.</returns>
    public IReadOnlyList<SessionConfigOption> SetConfig(string configId, JsonElement value)
    {
        AssertActive();
        return _modelControl.Set(configId, value);
    }

    /// <summary>Await every update queued before this call.</summary>
    /// <returns>settlement of the ordered update tail.</returns>
    public Task DrainUpdatesAsync() => _outputTail;

    /// <summary>
    /// Admit, enqueue, and settle one prompt at whole-Agent quiescence. The ported transport has
    /// no server-side request abort, so the <c>session/cancel</c> notification is the only prompt
    /// cancellation channel (documented reduction).
    /// </summary>
    /// <param name="prompt">the raw ACP prompt block array.</param>
    /// <returns>the correlated standard stop reason after ordered updates drain.</returns>
    public async Task<PromptResult> PromptAsync(JsonElement prompt)
    {
        AssertActive();
        var inflight = new InflightPrompt();
        lock (_gate)
        {
            if (_inflight is not null)
            {
                throw new JsonRpcResponseError(-32602, "a prompt is already in flight for this session");
            }
            _inflight = inflight;
        }
        try
        {
            if (!ReferenceEquals(_ctx.Get<Dsh.Agent.AgentRegistry>("agents")?.Get(Agent.Id), Agent))
            {
                throw new JsonRpcResponseError(-32603, "prompt was not queued: the agent was disposed outside the bridge");
            }
            var content = AcpContent.AdmitPrompt(prompt);
            var message = new UserMessage
            {
                Id = new MessageId(Guid.NewGuid().ToString("N")),
                Content = content,
                Source = new UserSource(),
            };
            inflight.MessageId = message.Id.Value;
            inflight.MessageQueued = true;
            try
            {
                _driver.Followup(message);
            }
            catch (Exception error)
            {
                inflight.MessageQueued = false;
                throw new JsonRpcResponseError(-32603, $"prompt was not queued: {error.Message}");
            }
        }
        catch (Exception admissionFailure)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inflight, inflight)) _inflight = null;
            }
            if (admissionFailure is JsonRpcResponseError wire) throw wire;
            throw new JsonRpcResponseError(-32603, $"prompt was not queued: {admissionFailure.Message}");
        }
        SettleAfterQuiescence(inflight);
        return new PromptResult(await inflight.Completion.Task);
    }

    /// <summary>Cancel the active prompt, or autonomous work when no ACP prompt exists.</summary>
    public void Cancel()
    {
        InflightPrompt? inflight;
        lock (_gate) inflight = _inflight;
        CancelPrompt("ACP prompt cancelled");
        if (inflight is null) _driver.Cancel(new UserCancel());
    }

    /// <summary>
    /// Process one durable event and enqueue its standard ACP projections.
    /// </summary>
    /// <param name="session">the exact event-owning Session.</param>
    /// <param name="evt">the committed durable event.</param>
    public void OnSessionEvent(Dsh.Session.Session session, SessionEvent evt)
    {
        try
        {
            switch (evt)
            {
                case AssistantMessageEvent assistant:
                {
                    InflightPrompt? inflight;
                    lock (_gate) inflight = _inflight?.Turn == assistant.Turn ? _inflight : null;
                    EnqueueUpdate(
                        () => DeliverAssistantUpdatesAsync(assistant),
                        failure =>
                        {
                            if (inflight is not null) inflight.OutputError ??= failure;
                            _ctx.Logger.Warn($"acp: assistant output conversion failed: {failure.Message}");
                        });
                    break;
                }
                case ToolCallEvent call:
                    EnqueueUpdate(
                        () => _notify(AcpUpdates.ToolCallUpdate(call)),
                        failure => _ctx.Logger.Warn($"acp: tool-call update delivery failed: {failure.Message}"));
                    break;
                case ToolResultEvent result:
                    EnqueueUpdate(
                        () => _notify(AcpUpdates.ToolResultUpdate(result)),
                        failure => _ctx.Logger.Warn($"acp: tool-result update delivery failed: {failure.Message}"));
                    break;
            }
        }
        finally
        {
            InflightPrompt? inflight;
            lock (_gate) inflight = _inflight;
            if (inflight is not null && evt is TurnEndEvent turnEnd && inflight.Turn == turnEnd.Turn)
            {
                inflight.EndReason = turnEnd.Reason;
            }
        }
    }

    /// <summary>Correlate an accepted user message with its Agent turn.</summary>
    /// <param name="message">the claimed durable inbox message.</param>
    /// <param name="turn">the allocated Agent turn.</param>
    public void OnInboxClaimed(UserMessage message, long turn)
    {
        lock (_gate)
        {
            if (_inflight is not null && _inflight.MessageId == message.Id.Value) _inflight.Turn = turn;
        }
    }

    /// <summary>Correlate an Agent interval failure with the active ACP prompt.</summary>
    /// <param name="turn">the failed turn number.</param>
    /// <param name="error">the original same-process failure.</param>
    public void OnAgentError(long turn, Exception error)
    {
        InflightPrompt? inflight;
        lock (_gate) inflight = _inflight;
        if (inflight is null || !inflight.MessageQueued) return;
        // The loop balances an in-turn failure with durable turn/end; settlement reads that exact
        // error reason. This slot records interval failures outside it.
        if (inflight.Turn == turn) return;
        inflight.AgentError = error;
        SettleAfterQuiescence(inflight);
    }

    /// <summary>
    /// Cancel, drain, and dispose this session once. The continuable-subagent drain and the
    /// per-session persistence flush are documented reductions (the port's subagent seam exposes
    /// no descendant drain; the spine's persistence row attaches the whole store).
    /// </summary>
    /// <param name="detail">the cancellation detail.</param>
    /// <returns>the shared quiescent teardown promise.</returns>
    public Task CloseAsync(string detail)
    {
        lock (_gate)
        {
            if (_closing is not null) return _closing;
            _closing = CloseCoreAsync(detail);
            return _closing;
        }
    }

    private async Task CloseCoreAsync(string detail)
    {
        var failures = new List<Exception>();
        InflightPrompt? inflight;
        lock (_gate) inflight = _inflight;
        CancelPrompt(detail);
        if (inflight is null || !inflight.MessageQueued) _driver.Cancel(new UserCancel());
        try
        {
            await _driver.WhenIdleAsync();
            await _outputTail;
        }
        catch (Exception error)
        {
            failures.Add(new Exception("ACP session activity drain failed", error));
        }
        try
        {
            _handle.Dispose();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
        {
            throw new AggregateException($"ACP session teardown failed: {string.Join("; ", failures.Select(error => error.Message))}", failures);
        }
    }

    private async Task DeliverAssistantUpdatesAsync(AssistantMessageEvent assistant)
    {
        foreach (var update in AcpUpdates.AssistantUpdates(assistant))
        {
            await _notify(update);
        }
    }

    /// <summary>Chain one ordered update delivery onto the tail, containing its failure.</summary>
    private void EnqueueUpdate(Func<Task> step, Action<Exception> onFailure)
    {
        var previous = _outputTail;
        _outputTail = previous
            .ContinueWith(_ => step(), TaskScheduler.Default).Unwrap()
            .ContinueWith(completed =>
            {
                if (completed.IsFaulted)
                {
                    onFailure(completed.Exception?.GetBaseException() ?? new Exception("update delivery failed"));
                }
            }, TaskScheduler.Default);
    }

    private void AssertActive()
    {
        lock (_gate)
        {
            if (_closing is not null)
            {
                throw new JsonRpcResponseError(-32602, $"session is closing: {Session.Id.Value}");
            }
        }
    }

    private void CancelPrompt(string detail)
    {
        InflightPrompt? inflight;
        lock (_gate) inflight = _inflight;
        if (inflight is null) return;
        inflight.CancelRequested = true;
        SettleAfterQuiescence(inflight);
        if (inflight.MessageQueued) _driver.Cancel(new UserCancel());
    }

    private void SettleAfterQuiescence(InflightPrompt inflight)
    {
        if (inflight.SettlementStarted) return;
        inflight.SettlementStarted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (inflight.MessageQueued)
                {
                    await _driver.WhenIdleAsync();
                    await _outputTail;
                }
                lock (_gate)
                {
                    if (!ReferenceEquals(_inflight, inflight)) return;
                    _inflight = null;
                }
                if (inflight.CancelRequested)
                {
                    inflight.Completion.TrySetResult("cancelled");
                    return;
                }
                if (inflight.OutputError is not null)
                {
                    inflight.Completion.TrySetException(new JsonRpcResponseError(-32603,
                        $"assistant output delivery failed: {inflight.OutputError.Message}"));
                    return;
                }
                if (inflight.AgentError is not null)
                {
                    inflight.Completion.TrySetException(new JsonRpcResponseError(-32603,
                        $"turn failed: {inflight.AgentError.Message}"));
                    return;
                }
                var end = inflight.EndReason;
                if (end is null)
                {
                    inflight.Completion.TrySetResult("cancelled");
                }
                else if (end is ErrorReason error)
                {
                    inflight.Completion.TrySetException(new JsonRpcResponseError(-32603,
                        $"turn failed: {error.Failure.Message}"));
                }
                else
                {
                    inflight.Completion.TrySetResult(AcpCodec.TurnEndToStopReason(end));
                }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_inflight, inflight)) _inflight = null;
                }
                inflight.Completion.TrySetException(new JsonRpcResponseError(-32603, $"prompt settlement failed: {error.Message}"));
            }
        });
    }
}
