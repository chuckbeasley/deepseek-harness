using System.Text.Json;

namespace Dsh.Subagent;

/// <summary>
/// One published SDK child run: the output fold over the child session events, the terminal
/// stop-reason mapping (port of <c>sdkChildOutcome</c>), and the idempotent dispose ladder.
/// The result promise never rejects after publication.
/// </summary>
internal sealed class SdkRun : ISubagentRun
{
    private readonly SubagentRequest _request;
    private readonly SdkChildConnection _connection;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeSpan _eofGrace;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<SubagentResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sessionId;
    private readonly AssistantOutputFold _fold = new();
    private readonly object _gate = new();
    private Task _teardown = Task.CompletedTask;
    private JsonElement? _lastEndReason;
    private int _settled;
    private int _disposed;

    public SdkRun(SubagentId id, SubagentRequest request, SdkChildConnection connection, TimeSpan shutdownTimeout, TimeSpan eofGrace, CancellationToken callerSignal)
    {
        Id = id;
        _request = request;
        _connection = connection;
        _shutdownTimeout = shutdownTimeout;
        _eofGrace = eofGrace;
        _sessionId = "session-" + Guid.NewGuid().ToString("N");
        connection.OnNotification = (method, parameters) => ObserveNotification(method, parameters);
        _ = WatchCancellationAsync(callerSignal);
    }

    public SubagentId Id { get; }

    public Task<SubagentResult> Result => _result.Task;

    /// <summary>Start the delegated turn.</summary>
    public void StartTurn()
    {
        _ = RunTurnAsync();
    }

    /// <summary>
    /// Settle the result locally (aborted when still running) and tear the child down:
    /// bounded shutdown exchange, EOF quiesce window, then tree kill and unbounded exit wait.
    /// Idempotent: every caller awaits the same quiescence boundary.
    /// </summary>
    public Task DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed != 0) return _teardown;
            _disposed = 1;
        }
        if (Interlocked.Exchange(ref _settled, 1) == 0)
        {
            _result.TrySetResult(new SubagentResult(_fold.Collect(), StopReason: SubagentStopReason.Aborted));
        }
        _lifetime.Cancel();
        _teardown = TeardownAsync();
        return _teardown;
    }

    private async Task RunTurnAsync()
    {
        try
        {
            // The prompt response only confirms admission; the turn boundary is the child's
            // `session.status: idle` notification (port of the SDK client's run loop).
            await _connection.PromptAsync(_sessionId, _request.Task, _lifetime.Token).ConfigureAwait(false);
            var completed = await Task.WhenAny(_idle.Task, _connection.Closed).ConfigureAwait(false);
            if (ReferenceEquals(completed, _idle.Task))
            {
                SettleFromReason(_lastEndReason);
            }
            else
            {
                // The child closed before its idle notification: transport loss, partial
                // output preserved.
                Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "transport"));
            }
        }
        catch (OperationCanceledException)
        {
            Settle(SubagentStopReason.Aborted, null);
        }
        catch (SdkTransportFailure)
        {
            Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "transport"));
        }
        catch (SdkProtocolFailure)
        {
            Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "protocol"));
        }
        catch (Exception)
        {
            Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "unknown"));
        }
    }

    /// <summary>Fold one session event: assistant messages, text-delta chunks, turn-end reasons, and idle.</summary>
    private void ObserveNotification(string method, JsonElement parameters)
    {
        if (method == "session.status")
        {
            if (parameters.TryGetProperty("sessionId", out var statusSession)
                && statusSession.GetString() == _sessionId
                && parameters.TryGetProperty("status", out var status)
                && status.GetString() == "idle")
            {
                _idle.TrySetResult(true);
            }
            return;
        }
        if (method != "session.event") return;
        if (!parameters.TryGetProperty("sessionId", out var sessionId) || sessionId.GetString() != _sessionId) return;
        if (!parameters.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object) return;
        var type = evt.TryGetProperty("type", out var typeJson) ? typeJson.GetString() : null;
        var data = evt.TryGetProperty("data", out var dataJson) ? dataJson : default;
        switch (type)
        {
            case "assistant/message":
                _fold.PushMessage(data);
                break;
            case "assistant/chunk":
                _fold.PushChunk(data);
                break;
            case "turn/end":
                _lastEndReason = data;
                break;
        }
    }

    /// <summary>Map the terminal reason envelope exactly like the TS <c>sdkChildOutcome</c>.</summary>
    private void SettleFromReason(JsonElement? reasonJson)
    {
        if (reasonJson is not JsonElement reason || !reason.TryGetProperty("reason", out var envelope))
        {
            Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "missing-terminal"));
            return;
        }
        var kind = envelope.TryGetProperty("kind", out var kindJson) ? kindJson.GetString() : null;
        switch (kind)
        {
            case "completed":
                Settle(SubagentStopReason.Completed, null);
                break;
            case "max-tokens":
                Settle(SubagentStopReason.MaxTokens, null);
                break;
            case "aborted":
                var cause = envelope.TryGetProperty("reason", out var causeJson)
                    && causeJson.TryGetProperty("kind", out var causeKind)
                        ? causeKind.GetString()
                        : null;
                Settle(SubagentStopReason.Aborted,
                    cause == "disposed" ? SdkFailure.Of("session-run", "child-disposed") : null);
                break;
            case "blocked":
                Settle(SubagentStopReason.Refusal, null);
                break;
            case "error":
                Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "child-error"));
                break;
            case "interrupted":
                Settle(SubagentStopReason.Error, null);
                break;
            case null:
                Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "missing-terminal"));
                break;
            default:
                Settle(SubagentStopReason.Error, SdkFailure.Of("session-run", "child-unknown"));
                break;
        }
    }

    /// <summary>Settle exactly once with the selected output and facts.</summary>
    private void Settle(SubagentStopReason stopReason, string? diagnostic)
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        _result.TrySetResult(new SubagentResult(
            _fold.Collect(),
            OutOfProcess.LimitDiagnostic(diagnostic),
            stopReason));
    }

    /// <summary>Local cancellation after publication: settle aborted, then tear the child down.</summary>
    private async Task WatchCancellationAsync(CancellationToken callerSignal)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerSignal, _lifetime.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested) return; // dispose already owns teardown
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task TeardownAsync()
    {
        // Rung 1: bounded shutdown exchange. A refusal is swallowed — the tree kill below is
        // authoritative either way.
        try
        {
            using var shutdown = new CancellationTokenSource(_shutdownTimeout);
            await _connection.ShutdownAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the child refused or the transport broke; the ladder continues
        }
        // Rung 2: EOF quiesce window.
        _connection.CloseStdin();
        if (await _connection.WaitForExitAsync(_eofGrace).ConfigureAwait(false)) return;
        // Rung 3: tree kill, then quiescence — no further timer after the kill is committed.
        _connection.KillTree();
        await _connection.WaitForExitAsync().ConfigureAwait(false);
        await _connection.ReapAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The child output selection rule (port of <c>AssistantOutputFold</c>): the last non-empty
/// assistant message wins; otherwise the accumulated streamed text.
/// </summary>
internal sealed class AssistantOutputFold
{
    private string? _message;
    private readonly List<string> _partial = new();

    /// <summary>Fold an <c>assistant/message</c> event: a non-empty content array becomes the candidate.</summary>
    public void PushMessage(JsonElement data)
    {
        if (!data.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        var text = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var blockText) && blockText.ValueKind == JsonValueKind.String)
            {
                text.Add(blockText.GetString() ?? string.Empty);
            }
        }
        var joined = string.Concat(text);
        if (joined.Length > 0) _message = joined;
    }

    /// <summary>Fold an <c>assistant/chunk</c> event: a text-delta chunk extends the streamed fallback.</summary>
    public void PushChunk(JsonElement data)
    {
        if (!data.TryGetProperty("chunk", out var chunk)) return;
        if (!chunk.TryGetProperty("type", out var type) || type.GetString() != "text-delta") return;
        if (!chunk.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return;
        var piece = text.GetString() ?? string.Empty;
        if (piece.Length > 0) _partial.Add(piece);
    }

    /// <summary>Select the final output: the last non-empty assistant message, else the streamed text.</summary>
    public string Collect() => _message ?? string.Concat(_partial);
}
