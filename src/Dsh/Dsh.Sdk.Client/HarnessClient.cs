using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Harness.Sdk.Protocol;

namespace Harness.Sdk.Client;

/// <summary>The runtime subprocess is gone or unusable: it exited, its stdio closed, or it was never launchable.</summary>
public sealed class TransportClosedError : Exception
{
    /// <summary>Create the transport-closed failure.</summary>
    /// <param name="message">the failure description, including any stderr tail.</param>
    public TransportClosedError(string message) : base(message) { }
}

/// <summary>A request exceeded the per-request timeout.</summary>
public sealed class RequestTimeoutError : Exception
{
    /// <summary>Create the timeout failure.</summary>
    /// <param name="message">which method timed out.</param>
    public RequestTimeoutError(string message) : base(message) { }
}

/// <summary>The runtime answered outside its documented protocol (for example a <c>session/prompt</c> response without a message id).</summary>
public sealed class SdkProtocolError : Exception
{
    /// <summary>Create the protocol-violation failure.</summary>
    /// <param name="message">the protocol violation description.</param>
    public SdkProtocolError(string message) : base(message) { }
}

/// <summary>One client-side notification stream returned by <see cref="HarnessClient.Subscribe"/>.</summary>
public sealed class NotificationSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<HarnessNotification> _queue = new();
    private readonly List<TaskCompletionSource<HarnessNotification>> _waiters = new();
    private readonly NotificationFilter? _filter;
    private readonly Action _unsubscribe;
    private Exception? _failure;

    internal NotificationSubscription(NotificationFilter? filter, Action unsubscribe)
    {
        _filter = filter;
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
    }

    /// <summary>
    /// Await the next matching notification. After the runtime died, drains what was already
    /// delivered and then rejects; after <see cref="Dispose"/>, rejects immediately (the queue is
    /// dropped).
    /// </summary>
    /// <returns>the next matching notification.</returns>
    public Task<HarnessNotification> NextAsync()
    {
        lock (_gate)
        {
            if (_queue.Count > 0) return Task.FromResult(_queue.Dequeue());
            if (_failure is not null) return Task.FromException<HarnessNotification>(_failure);
            var waiter = new TaskCompletionSource<HarnessNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
            return waiter.Task;
        }
    }

    /// <summary>Drain one already-delivered notification without waiting.</summary>
    /// <returns>the next queued notification, or <c>null</c> when none is queued.</returns>
    public HarnessNotification? TryNext()
    {
        lock (_gate) return _queue.Count > 0 ? _queue.Dequeue() : null;
    }

    /// <summary>Detach from the client; queued items drop and pending waiters reject.</summary>
    public void Dispose()
    {
        _unsubscribe();
        lock (_gate)
        {
            // The drop is part of this contract; a runtime-death Fail keeps the queue so
            // already-delivered notifications remain drainable.
            _queue.Clear();
            FailLocked(new TransportClosedError("notification subscription closed"));
        }
    }

    /// <summary>
    /// Deliver one notification to a waiter or the queue when the filter matches. A throwing
    /// filter fails only THIS subscription (detached, the throw becomes its terminal error) — it
    /// never disturbs sibling subscriptions or the transport's read loop.
    /// </summary>
    /// <param name="notification">the wire notification to deliver.</param>
    internal void Push(HarnessNotification notification)
    {
        bool matches;
        try
        {
            matches = _filter is null || _filter(notification);
        }
        catch (Exception error)
        {
            _unsubscribe();
            lock (_gate) FailLocked(error);
            return;
        }
        if (!matches) return;
        lock (_gate)
        {
            if (_waiters.Count > 0)
            {
                var waiter = _waiters[0];
                _waiters.RemoveAt(0);
                waiter.TrySetResult(notification);
            }
            else
            {
                _queue.Enqueue(notification);
            }
        }
    }

    /// <summary>Reject pending and future waits (delivery stops; the first failure wins). Already-queued notifications remain drainable.</summary>
    /// <param name="error">the terminal failure delivered to waiters.</param>
    internal void Fail(Exception error)
    {
        lock (_gate) FailLocked(error);
    }

    private void FailLocked(Exception error)
    {
        _failure ??= error;
        foreach (var waiter in _waiters) waiter.TrySetException(_failure);
        _waiters.Clear();
    }
}

/// <summary>
/// JSON-RPC client for the DeepSeek Harness SDK runtime over subprocess stdio (port of the TS
/// <c>HarnessClient</c>; the design twin of the Python SDK's client). The subprocess starts lazily
/// on <see cref="Start"/> and is owned by this instance until <see cref="CloseAsync"/>, which
/// requests protocol <c>shutdown</c> and then walks the shared stdin-EOF → kill dispose ladder to
/// quiescence. There is no wire-level cancel: a timed-out request stays running server-side until
/// the runtime is closed. This client runs OUTSIDE any harness context, so it spawns the runtime
/// directly — the subprocess seam's documented exception for SDK-managed transports.
/// </summary>
public sealed class HarnessClient : IDisposable, IAsyncDisposable
{
    private const int StderrTailLimit = 400;
    private static readonly TimeSpan StreamSettle = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions WireJson = CreateWireJson();

    private static JsonSerializerOptions CreateWireJson()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new SdkPromptContentBlockConverter());
        return options;
    }

    private readonly RuntimeProcessOptions _runtime;
    private readonly List<string> _stderrTail = new();
    private readonly Dictionary<string, NotificationSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly SessionLineage _lineage = new();
    private readonly object _gate = new();
    private Process? _child;
    private JsonRpcLineTransport? _transport;
    private Exception? _spawnError;
    private int? _exitCode;
    private bool _exited;
    private bool _closed;
    private int _subscriptionSerial;
    private Task _stderrReader = Task.CompletedTask;
    private Task? _closeTask;

    /// <summary>Create the client over one resolved launch; <see cref="SdkLaunch.ResolveLaunch"/> supplies the default.</summary>
    /// <param name="options">dsh profile, patch, home, process, environment, and timeout options.</param>
    /// <param name="runtime">resolved process spec; omitted resolves from <paramref name="options"/>.</param>
    public HarnessClient(HarnessClientOptions? options = null, RuntimeProcessOptions? runtime = null)
    {
        Options = options ?? new HarnessClientOptions();
        _runtime = runtime ?? SdkLaunch.ResolveLaunch(Options, Environment.CurrentDirectory);
    }

    /// <summary>Original public dsh launch and timeout options for this client.</summary>
    public HarnessClientOptions Options { get; }

    /// <summary>
    /// Spawn the runtime subprocess and start reading frames. Idempotent while the process is
    /// live; rejects reuse after <see cref="CloseAsync"/>.
    /// </summary>
    public void Start()
    {
        if (_closed) throw new TransportClosedError("DeepSeek Harness runtime client is closed");
        lock (_gate)
        {
            if (_child is not null) return;
        }
        var info = new ProcessStartInfo
        {
            FileName = _runtime.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in _runtime.Args) info.ArgumentList.Add(arg);
        if (_runtime.WorkingDirectory is not null) info.WorkingDirectory = _runtime.WorkingDirectory;
        info.Environment.Clear();
        foreach (var (name, value) in _runtime.Environment()) info.Environment[name] = value;
        var child = new Process { StartInfo = info };
        try
        {
            child.Start();
        }
        catch (Exception error)
        {
            // A spawn failure leaves no process to reap and no pipes to read; every request then
            // fails with this context.
            _spawnError = error;
            FailSubscriptions(ClosedError("DeepSeek Harness runtime failed to start"));
            return;
        }
        _child = child;
        _stderrReader = ReadStderrAsync(child);
        var transport = new JsonRpcLineTransport(child.StandardOutput.BaseStream, child.StandardInput.BaseStream);
        transport.OnNotification(DispatchNotification);
        transport.Start();
        _transport = transport;
        _ = WatchExitAsync(child);
    }

    /// <summary>Perform the process-wide handshake.</summary>
    /// <param name="parameters">workspace cwd plus the provider/model route.</param>
    /// <returns>the runtime's wire identity.</returns>
    public async Task<InitializeResult> InitializeAsync(InitializeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var result = await RequestAsync(SdkProtocol.Initialize, parameters, _runtime.InitializeTimeoutMs).ConfigureAwait(false);
        if (result is not JsonElement element
            || !element.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !info.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
        {
            throw new SdkProtocolError($"initialize returned no server identity: {JsonSerializer.Serialize(result)}");
        }
        return new InitializeResult(new ServerInfo(name.GetString()!, version.GetString()!));
    }

    /// <summary>Queue one prompt and return its durable inbox identity.</summary>
    /// <param name="sessionId">target session; an unknown id creates it.</param>
    /// <param name="contentBlocks">the user message, sent verbatim.</param>
    /// <returns>the queued message id.</returns>
    public async Task<string> PromptAsync(string sessionId, IReadOnlyList<SdkPromptContentBlock> contentBlocks)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(contentBlocks);
        var result = await RequestAsync(SdkProtocol.SessionPrompt, new { sessionId, contentBlocks }).ConfigureAwait(false);
        if (result is not JsonElement element
            || !element.TryGetProperty("messageId", out var messageId) || messageId.ValueKind != JsonValueKind.String)
        {
            throw new SdkProtocolError($"session/prompt returned no message id: {JsonSerializer.Serialize(result)}");
        }
        return messageId.GetString()!;
    }

    /// <summary>
    /// Send one JSON-RPC request and await its result.
    /// </summary>
    /// <param name="method">the wire method name.</param>
    /// <param name="paramsValue">the params object; omitted params send <c>{}</c>.</param>
    /// <param name="timeoutMs">per-call override of <see cref="HarnessClientOptions.RequestTimeoutMs"/>.</param>
    /// <returns>the raw result; rejects with <see cref="JsonRpcResponseError"/> on a protocol error
    /// response, <see cref="RequestTimeoutError"/> on timeout, and <see cref="TransportClosedError"/>
    /// when the runtime is gone.</returns>
    public async Task<JsonElement?> RequestAsync(string method, object? paramsValue = null, int? timeoutMs = null)
    {
        Start();
        var transport = _transport;
        if (_exited || _spawnError is not null || transport is null)
        {
            await SettleStreamsAsync().ConfigureAwait(false);
            throw ClosedError("DeepSeek Harness runtime is not running");
        }
        var timeout = timeoutMs ?? _runtime.RequestTimeoutMs;
        var wireParams = paramsValue is JsonElement element ? element : JsonSerializer.SerializeToElement(paramsValue ?? new { }, WireJson);
        try
        {
            if (timeout is null)
            {
                return await transport.RequestAsync(method, wireParams).ConfigureAwait(false);
            }
            using var cts = new CancellationTokenSource(timeout.Value);
            try
            {
                return await transport.RequestAsync(method, wireParams, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The timeout is an abandonment: the transport drops its pending entry, so repeated
                // bounded requests against a hung method retain no per-call state (the server-side
                // work still runs to close).
                var stderr = _stderrTail.Count == 0 ? "" : $"; stderr tail:\n{string.Join("\n", _stderrTail)}";
                throw new RequestTimeoutError(
                    $"{method} timed out after {timeout.Value}ms waiting for {_runtime.Description}{stderr}");
            }
        }
        catch (Exception error) when (error is not JsonRpcResponseError and not RequestTimeoutError)
        {
            // Transport-level failures gain process context: exit code + stderr tail.
            await SettleStreamsAsync().ConfigureAwait(false);
            throw ClosedError(error.Message);
        }
    }

    /// <summary>
    /// Subscribe to server notifications.
    /// </summary>
    /// <param name="filter">optional predicate; omitted means every notification.</param>
    /// <returns>the subscription handle; dispose it to stop delivery. After <see cref="CloseAsync"/>
    /// or runtime death the handle is born failed — there is no producer left, so
    /// <see cref="NotificationSubscription.NextAsync"/> rejects instead of waiting forever.</returns>
    public NotificationSubscription Subscribe(NotificationFilter? filter = null)
    {
        lock (_gate)
        {
            var id = "sub-" + _subscriptionSerial++;
            var subscription = new NotificationSubscription(filter, () =>
            {
                lock (_gate) _subscriptions.Remove(id);
            });
            if (_closed || _exited || _spawnError is not null)
            {
                subscription.Fail(ClosedError("DeepSeek Harness runtime closed"));
            }
            else
            {
                _subscriptions[id] = subscription;
            }
            return subscription;
        }
    }

    /// <summary>
    /// Subscribe to one session and the descendants discovered from <c>subagent.started</c>
    /// lineage edges. The runtime notifies for every session in its context, so this client
    /// applies the scope.
    /// </summary>
    /// <param name="sessionId">the root session id.</param>
    /// <returns>the filtered subscription handle.</returns>
    public NotificationSubscription SubscribeSessionTree(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return Subscribe(notification =>
        {
            if (notification.Method is SdkProtocol.SubagentStarted or SdkProtocol.SubagentFinished)
            {
                var parentId = ParamsString(notification.Params, "parentSessionId");
                if (_lineage.IsDescendantOf(parentId, sessionId)) return true;
                return ParamsString(notification.Params, "childSessionId") == sessionId;
            }
            return _lineage.IsDescendantOf(ParamsString(notification.Params, "sessionId"), sessionId);
        });
    }

    /// <summary>
    /// Shut the runtime down and reap it: a best-effort protocol <c>shutdown</c> bounded by
    /// <c>shutdownTimeoutMs</c>, then the shared stdin-EOF → kill ladder until the process actually
    /// exited (Windows has no graceful signal, so the forced kill follows the EOF window directly;
    /// the TS skips SIGTERM there too). Idempotent.
    /// </summary>
    /// <returns>settlement of the complete teardown.</returns>
    public Task CloseAsync()
    {
        _closeTask ??= PerformCloseAsync();
        return _closeTask;
    }

    /// <inheritdoc />
    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task PerformCloseAsync()
    {
        var child = _child;
        if (child is null) return;
        try
        {
            await RequestAsync(SdkProtocol.Shutdown, null, _runtime.ShutdownTimeoutMs).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Diagnostic only: the dispose ladder below is the authoritative teardown for a runtime
            // that cannot answer shutdown anymore.
            AppendStderrLine($"shutdown request failed: {error.Message}");
        }
        // Rung 1: stdin EOF and cooperative quiesce (the sdk runtime exits on EOF).
        CloseStdin(child);
        if (!await WaitForExitAsync(child, TimeSpan.FromMilliseconds(_runtime.DisposeEofGraceMs)).ConfigureAwait(false))
        {
            // Rung 2: forced termination, then quiescence.
            KillTree(child);
            await WaitForExitAsync(child, TimeSpan.FromMilliseconds(_runtime.DisposeGraceMs)).ConfigureAwait(false);
        }
        _transport?.Close();
        _closed = true;
        // Capture the code before disposal so closed-error messages carry it deterministically.
        _exitCode ??= SafeExitCode(child);
        FailSubscriptions(ClosedError("DeepSeek Harness runtime closed"));
        child.Dispose();
    }

    private void DispatchNotification(string method, JsonElement? parameters)
    {
        var notification = new HarnessNotification(method, parameters ?? JsonSerializer.SerializeToElement(new { }));
        _lineage.Record(notification);
        NotificationSubscription[] subscriptions;
        lock (_gate) subscriptions = _subscriptions.Values.ToArray();
        foreach (var subscription in subscriptions) subscription.Push(notification);
    }

    private void FailSubscriptions(Exception error)
    {
        NotificationSubscription[] subscriptions;
        lock (_gate) subscriptions = _subscriptions.Values.ToArray();
        foreach (var subscription in subscriptions) subscription.Fail(error);
    }

    private void AppendStderrLine(string line)
    {
        lock (_gate)
        {
            _stderrTail.Add(line);
            if (_stderrTail.Count > StderrTailLimit) _stderrTail.RemoveRange(0, _stderrTail.Count - StderrTailLimit);
        }
    }

    private TransportClosedError ClosedError(string reason)
    {
        var parts = new List<string> { $"{_runtime.Description}: {reason}" };
        if (_spawnError is not null) parts.Add($"spawn error: {_spawnError.Message}");
        if (_exitCode is not null) parts.Add($"exit code: {_exitCode.Value}");
        if (_stderrTail.Count > 0) parts.Add($"stderr tail:\n{string.Join("\n", _stderrTail)}");
        return new TransportClosedError(string.Join("\n", parts));
    }

    private async Task SettleStreamsAsync()
    {
        await Task.WhenAny(_stderrReader, Task.Delay(StreamSettle)).ConfigureAwait(false);
    }

    private async Task ReadStderrAsync(Process child)
    {
        try
        {
            using var reader = new StreamReader(child.StandardError.BaseStream, new UTF8Encoding(false));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                AppendStderrLine(line);
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostics only; the exit edge is the real signal.
        }
    }

    private async Task WatchExitAsync(Process child)
    {
        try
        {
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The process handle failed to report exit; the stdout EOF edge still fires.
        }
        try
        {
            _exitCode = child.ExitCode;
        }
        catch (Exception)
        {
            // Process disposed concurrently; the code is diagnostic only.
        }
        _exited = true;
        FailSubscriptions(ClosedError("DeepSeek Harness runtime exited"));
    }

    private static async Task<bool> WaitForExitAsync(Process child, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void CloseStdin(Process child)
    {
        try
        {
            child.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
            // stream already closed
        }
    }

    private static void KillTree(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // tree gone
        }
    }

    private static int? SafeExitCode(Process child)
    {
        try
        {
            return child.ExitCode;
        }
        catch (Exception)
        {
            // process already disposed; the code is diagnostic only
            return null;
        }
    }

    private static string ParamsString(JsonElement parameters, string key)
        => parameters.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>The subagent lineage map behind <see cref="HarnessClient.SubscribeSessionTree"/> (port of the TS parent map).</summary>
internal sealed class SessionLineage
{
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Record one <c>subagent.started</c> lineage edge (child → parent).</summary>
    public void Record(HarnessNotification notification)
    {
        if (notification.Method != SdkProtocol.SubagentStarted) return;
        var parentId = ParamsString(notification.Params, "parentSessionId");
        var childId = ParamsString(notification.Params, "childSessionId");
        if (parentId.Length > 0 && childId.Length > 0 && parentId != childId)
        {
            lock (_gate) _parents[childId] = parentId;
        }
    }

    /// <summary>Whether <paramref name="sessionId"/> equals <paramref name="rootSessionId"/> or walks to it through recorded parents.</summary>
    public bool IsDescendantOf(string sessionId, string rootSessionId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = sessionId;
        while (visited.Add(current))
        {
            if (current == rootSessionId) return true;
            string? parent;
            lock (_gate)
            {
                if (!_parents.TryGetValue(current, out parent)) return false;
            }
            current = parent;
        }
        // The parent map only ever extends chains upward, so a cycle cannot form.
        return false;
    }

    private static string ParamsString(JsonElement parameters, string key)
        => parameters.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
