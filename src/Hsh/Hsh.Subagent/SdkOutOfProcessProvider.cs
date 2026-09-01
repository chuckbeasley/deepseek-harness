using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Harness.Subagent;

/// <summary>Configuration for the HSH SDK out-of-process driver; validated once at construction.</summary>
public sealed record SdkOutOfProcessConfig(
    /// <summary>Child runtime entry: a .dll spawned via <c>dotnet</c>, or an apphost executable; required.</summary>
    string HshBin,
    /// <summary>Child profile name (default <c>sdk</c>).</summary>
    string Profile,
    /// <summary>Additional patch files applied to the child profile, each file-checked at load.</summary>
    IReadOnlyList<string> Patches,
    /// <summary>Absolute child harness home (<c>HSH_HOME</c>).</summary>
    string HshHome,
    /// <summary>Optional absolute child working directory; omission uses the parent working directory.</summary>
    string? Cwd,
    /// <summary>Provider route the child runs under.</summary>
    string Provider,
    /// <summary>Provider-owned model id the child runs under.</summary>
    string Model,
    /// <summary>Optional positive output-token cap.</summary>
    int? MaxTokens,
    /// <summary>Extra child environment entries merged AFTER the ambient scrub.</summary>
    IReadOnlyDictionary<string, string> Env,
    /// <summary>
    /// Extra child argv appended after the profile patches. The test seam: a compliant child
    /// (the fixture) selects its scripted mode through this slot.
    /// </summary>
    IReadOnlyList<string> AdditionalArgs,
    /// <summary>Bound for the shutdown exchange during disposal.</summary>
    int ShutdownTimeoutMs = 1000,
    /// <summary>Bound for the EOF quiesce window after stdin closes.</summary>
    int DisposeEofGraceMs = 6000,
    /// <summary>Bound for the termination escalation slot (a no-op window on Windows).</summary>
    int DisposeGraceMs = 3000);

/// <summary>
/// The HSH SDK out-of-process subagent driver (provider name <c>hsh-sdk</c>; port of
/// <c>@deepseek-ai/hsh-subagent-hsh-sdk</c>): spawns a complete child runtime per delegation,
/// drives it over newline-delimited JSON-RPC on stdio (initialize / session/prompt / shutdown),
/// and folds the child session events into the final result. The child side of the contract is
/// the SDK runtime server, which arrives with the SDK phase; until then a compliant child (the
/// test fixture) exercises the driver, and a non-compliant child fails loud with safe
/// stage/category facts.
/// </summary>
public sealed class SdkOutOfProcessProvider : ISubagentProvider
{
    private readonly SdkOutOfProcessConfig _config;

    /// <summary>Create the driver; configuration is validated here, never at start time.</summary>
    public SdkOutOfProcessProvider(SdkOutOfProcessConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.ShutdownTimeoutMs <= 0 || config.DisposeEofGraceMs <= 0 || config.DisposeGraceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "timing bounds must be positive");
        }
        if (!Path.IsPathFullyQualified(config.HshHome))
        {
            throw new ArgumentException($"subagent hshHome must be absolute, got \"{config.HshHome}\"", nameof(config));
        }
        if (!File.Exists(config.HshBin))
        {
            throw new ArgumentException($"subagent hshBin \"{config.HshBin}\" does not exist", nameof(config));
        }
        foreach (var patch in config.Patches)
        {
            if (!File.Exists(patch))
            {
                throw new ArgumentException($"subagent patch \"{patch}\" does not exist", nameof(config));
            }
        }
        if (config.Cwd is not null && !Path.IsPathFullyQualified(config.Cwd))
        {
            throw new ArgumentException($"subagent cwd must be absolute, got \"{config.Cwd}\"", nameof(config));
        }
        if (config.MaxTokens is int maxTokens && maxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "maxTokens must be positive");
        }
    }

    /// <inheritdoc />
    public string Name => "hsh-sdk";

    /// <inheritdoc />
    public SubagentCapabilities Capabilities => SubagentCapabilities.None with { AgentOptions = true };

    /// <inheritdoc />
    public bool InheritsParentContext => false;

    /// <inheritdoc />
    public (string Provider, string Model)? AgentRouteDefaults => (_config.Provider, _config.Model);

    /// <inheritdoc />
    public Task<ISubagentRun> StartAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Task.Trim().Length == 0)
        {
            throw new ArgumentException("subagent: the task must be a non-empty string", nameof(request));
        }
        return StartAsyncCore(request, cancellationToken);
    }

    private async Task<ISubagentRun> StartAsyncCore(SubagentRequest request, CancellationToken cancellationToken)
    {
        // Withdrawn before it began: never spawn — a cancelled child nobody reaps would linger.
        cancellationToken.ThrowIfCancellationRequested();
        var cwd = OutOfProcess.ResolveChildCwd(_config.Cwd);
        var child = SdkChildConnection.Spawn(_config, cwd);
        try
        {
            await child.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await child.ReapAsync().ConfigureAwait(false);
            if (error is OperationCanceledException)
            {
                throw new OperationCanceledException(
                    "subagent request was aborted before the SDK child started", error, cancellationToken);
            }
            throw new SubagentError(error.Message, "START_FAILED");
        }
        var run = new SdkRun(
            new SubagentId("subagent-sdk-" + Guid.NewGuid().ToString("N")),
            request,
            child,
            TimeSpan.FromMilliseconds(_config.ShutdownTimeoutMs),
            TimeSpan.FromMilliseconds(_config.DisposeEofGraceMs),
            cancellationToken);
        run.StartTurn();
        return run;
    }
}

/// <summary>Safe failure facts for one stage/category pair (never raw errors, env values, or paths).</summary>
internal static class SdkFailure
{
    public const string ProviderLabel = "HSH SDK";

    public static string Of(string stage, string category)
        => $"Subagent failure (provider: {ProviderLabel}; stage: {stage}; category: {category})";
}

/// <summary>One child runtime connection: newline-delimited JSON-RPC 2.0 over stdio pipes.</summary>
internal sealed class SdkChildConnection
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private Task _reader = Task.CompletedTask;
    private long _nextId;
    private string? _fatal;

    private SdkChildConnection(Process process, StreamWriter writer)
    {
        _process = process;
        _writer = writer;
    }

    /// <summary>
    /// Spawn the child with the scrubbed environment and begin reading its stdout. The
    /// notification handler must be set before the first prompt; the child cannot emit
    /// notifications before one is sent.
    /// </summary>
    public static SdkChildConnection Spawn(SdkOutOfProcessConfig config, string cwd)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WorkingDirectory = cwd,
        };
        var scrub = OutOfProcess.ScrubEnvironment(Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty));
        foreach (var (name, value) in scrub) info.Environment[name] = value;
        foreach (var (name, value) in config.Env) info.Environment[name] = value;
        info.Environment["HSH_HOME"] = config.HshHome;
        if (config.HshBin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = "dotnet";
            info.ArgumentList.Add(config.HshBin);
        }
        else
        {
            info.FileName = config.HshBin;
        }
        info.ArgumentList.Add("--profile");
        info.ArgumentList.Add(config.Profile);
        foreach (var patch in config.Patches)
        {
            info.ArgumentList.Add("--patch");
            info.ArgumentList.Add(patch);
        }
        foreach (var extra in config.AdditionalArgs)
        {
            info.ArgumentList.Add(extra);
        }
        var child = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            child.Start();
        }
        catch (Exception error)
        {
            throw new SubagentError(SdkFailure.Of("initialize", "process-start"), "START_FAILED", error);
        }
        var writer = new StreamWriter(child.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        var connection = new SdkChildConnection(child, writer);
        connection._reader = connection.ReadLoopAsync();
        return connection;
    }

    /// <summary>The notification handler receives (method, params) for every inbound notification.</summary>
    public Action<string, JsonElement>? OnNotification
    {
        get
        {
            lock (_gate) return _onNotification;
        }
        set
        {
            lock (_gate) _onNotification = value;
        }
    }

    private Action<string, JsonElement>? _onNotification;

    /// <summary>Resolves when the read loop ends (child stdout closed or a fatal protocol failure).</summary>
    public Task Closed => _closed.Task;

    /// <summary>Send initialize and await the handshake result (serverInfo presence validated).</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        JsonElement? result;
        try
        {
            result = await RequestAsync("initialize", null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkProtocolFailure error)
        {
            throw new SubagentError(SdkFailure.Of("initialize", "protocol"), "START_FAILED", error);
        }
        catch (SdkTransportFailure error)
        {
            throw new SubagentError(SdkFailure.Of("initialize", "transport"), "START_FAILED", error);
        }
        if (result is not JsonElement element || !element.TryGetProperty("serverInfo", out _))
        {
            throw new SubagentError(SdkFailure.Of("initialize", "protocol"), "START_FAILED");
        }
    }

    /// <summary>
    /// Send one turn; the terminal state arrives as notifications, the response only confirms
    /// admission (a missing <c>messageId</c> is a protocol failure).
    /// </summary>
    public async Task PromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("session/prompt", new
        {
            sessionId,
            prompt = new object[] { new { type = "text", text = prompt } },
        }, cancellationToken).ConfigureAwait(false);
        if (result is not JsonElement element
            || !element.TryGetProperty("messageId", out var messageId)
            || messageId.ValueKind != JsonValueKind.String)
        {
            throw new SdkProtocolFailure("the SDK child answered session/prompt without a messageId");
        }
    }

    /// <summary>Send shutdown (bounded by the caller).</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync("shutdown", null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SubagentError(SdkFailure.Of("shutdown", "transport"), "SHUTDOWN_FAILED", error);
        }
    }

    /// <summary>Close stdin (EOF) so a cooperative child quiesces.</summary>
    public void CloseStdin()
    {
        try
        {
            _writer.Close();
        }
        catch (InvalidOperationException)
        {
            // stream already closed
        }
    }

    /// <summary>Wait for the child process to exit, bounded by <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Wait for the child process to exit, unbounded.</summary>
    public Task WaitForExitAsync() => _process.WaitForExitAsync();

    /// <summary>Kill the child process tree (idempotent).</summary>
    public void KillTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
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

    /// <summary>Await the reader loop and dispose process resources.</summary>
    public async Task ReapAsync()
    {
        KillTree();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the reader ends with the process; nothing further to observe
        }
        _process.Dispose();
        _writer.Dispose();
        _writeGate.Dispose();
    }

    /// <summary>Send one request and await its response.</summary>
    private async Task<JsonElement?> RequestAsync(string method, object? requestParams, CancellationToken cancellationToken)
    {
        long id;
        TaskCompletionSource<JsonElement?> completion;
        lock (_gate)
        {
            if (_fatal is not null) throw new SdkTransportFailure(_fatal);
            id = ++_nextId;
            completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
        }
        try
        {
            var envelope = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = requestParams,
            };
            var line = JsonSerializer.Serialize(envelope, WireJson);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _pending.Remove(id);
            throw;
        }
        catch (Exception error) when (error is not SdkTransportFailure and not SdkProtocolFailure)
        {
            lock (_gate) _pending.Remove(id);
            throw new SdkTransportFailure(error.Message);
        }
    }

    /// <summary>Read loop: parse each line, complete pending requests, dispatch notifications.</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.StandardOutput.BaseStream, new UTF8Encoding(false));
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                Frame frame;
                try
                {
                    frame = JsonSerializer.Deserialize<Frame>(line, WireJson) ?? throw new JsonException("empty frame");
                }
                catch (JsonException error)
                {
                    FailAll(new SdkProtocolFailure($"child sent a malformed JSON-RPC frame: {error.Message}"));
                    return;
                }
                if (frame.Id is long responseId)
                {
                    TaskCompletionSource<JsonElement?> completion;
                    lock (_gate)
                    {
                        if (!_pending.Remove(responseId, out var found))
                        {
                            continue; // stray response for an unknown id: drop silently
                        }
                        completion = found;
                    }
                    if (frame.Error is JsonElement error && error.ValueKind == JsonValueKind.Object)
                    {
                        var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                            ? text.GetString() ?? "LSP error response"
                            : "LSP error response";
                        completion.TrySetException(new SdkProtocolFailure(message));
                    }
                    else
                    {
                        completion.TrySetResult(frame.Result);
                    }
                }
                else if (frame.Method is string method)
                {
                    var handler = OnNotification;
                    if (handler is not null)
                    {
                        handler(method, frame.Params ?? JsonSerializer.SerializeToElement(new { }));
                    }
                }
            }
        }
        catch (Exception error)
        {
            FailAll(new SdkTransportFailure(error.Message));
            return;
        }
        FailAll(new SdkTransportFailure("the SDK child closed its stdout"));
        _closed.TrySetResult(true);
    }

    /// <summary>Record the first fatal reason and reject every pending request with it.</summary>
    private void FailAll(Exception reason)
    {
        TaskCompletionSource<JsonElement?>[] pending;
        lock (_gate)
        {
            if (_fatal is not null) return;
            _fatal = reason.Message;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var completion in pending) completion.TrySetException(reason);
    }

    /// <summary>The parsed JSON-RPC frame envelope.</summary>
    private sealed class Frame
    {
        public long? Id { get; set; }

        public string? Method { get; set; }

        public JsonElement? Params { get; set; }

        public JsonElement? Result { get; set; }

        public JsonElement? Error { get; set; }
    }
}

/// <summary>The child closed its stdout or the write path broke before the protocol could fail.</summary>
internal sealed class SdkTransportFailure : Exception
{
    public SdkTransportFailure(string message)
        : base(message)
    {
    }
}

/// <summary>The child answered with a JSON-RPC error or a malformed frame.</summary>
internal sealed class SdkProtocolFailure : Exception
{
    public SdkProtocolFailure(string message)
        : base(message)
    {
    }
}
