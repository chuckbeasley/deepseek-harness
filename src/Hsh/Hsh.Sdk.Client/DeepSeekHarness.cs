using Harness.Sdk.Protocol;

namespace Harness.Sdk.Client;

/// <summary>
/// High-level run API over <see cref="HarnessClient"/> (port of the TS <c>DeepSeekHarness</c>):
/// owns one runtime subprocess across many sessions; <see cref="HarnessSession.RunAsync"/> sends a
/// prompt and settles when the whole agent next becomes idle. The subprocess starts lazily on
/// first use and stays owned by this instance until <see cref="CloseAsync"/>; always close (or
/// dispose) so the child is reaped.
/// </summary>
public sealed class DeepSeekHarness : IAsyncDisposable, IDisposable
{
    private readonly Func<HarnessClient> _createClient;
    private readonly string _cwd;
    private readonly string _provider;
    private readonly string _model;
    private readonly string? _reasoningEffort;
    private readonly int? _maxTokens;
    private HarnessClient _client;
    private Task? _initialized;
    private bool _closed;

    /// <summary>Create the harness.</summary>
    /// <param name="options">hsh launch configuration plus the session route, effort, and output cap.</param>
    /// <param name="clientFactory">client factory; omitted builds one over the launch options (the test seam).</param>
    public DeepSeekHarness(DeepSeekHarnessOptions? options = null, Func<HarnessClient>? clientFactory = null)
    {
        var resolved = options ?? new DeepSeekHarnessOptions();
        _createClient = clientFactory ?? (() => new HarnessClient(resolved));
        _client = _createClient();
        // Absolute before the handshake: the child spawns relative to THIS process's cwd, but the
        // wire cwd is resolved again inside the child — a relative value would double-resolve.
        _cwd = Path.GetFullPath(resolved.Cwd ?? resolved.ProcessCwd ?? Environment.CurrentDirectory);
        _provider = resolved.Provider ?? "deepseek-official";
        _model = resolved.Model ?? "deepseek-v4-flash";
        _reasoningEffort = resolved.ReasoningEffort;
        _maxTokens = resolved.MaxTokens;
    }

    /// <summary>
    /// The underlying JSON-RPC client (exposed for low-level access). A failed handshake swaps in
    /// a fresh instance only after cleanup proves the runtime exited; cleanup failure retains this
    /// client, so do not cache it across a failed <see cref="StartAsync"/>.
    /// </summary>
    /// <returns>the client currently owning the runtime subprocess.</returns>
    public HarnessClient Client => _client;

    /// <summary>
    /// Start the subprocess and perform the <c>initialize</c> handshake once. On failure,
    /// successful SDK-owned cleanup reaps the runtime and installs a fresh client
    /// (<see cref="HarnessClient.CloseAsync"/> is permanent), so a later call retries with a new
    /// subprocess unless <see cref="CloseAsync"/> already ended this harness.
    /// </summary>
    /// <returns>settlement of the (memoized) handshake.</returns>
    public Task StartAsync() => _initialized ??= StartCoreAsync();

    /// <summary>Open a session handle (no wire traffic; the runtime creates the session on its first prompt).</summary>
    /// <param name="sessionId">explicit id to reuse; omitted mints a fresh one.</param>
    /// <returns>the session handle.</returns>
    public HarnessSession Session(string? sessionId = null)
        => new(this, sessionId ?? "session-" + Guid.NewGuid().ToString("N"));

    /// <summary>Run one prompt on a fresh (or named) session.</summary>
    /// <param name="input">the prompt text.</param>
    /// <param name="options">optional session id and per-notification observer.</param>
    /// <returns>the owned activity interval.</returns>
    public Task<RunResult> RunAsync(string input, RunOptions? options = null)
        => Session(options?.SessionId).RunAsync(input, options);

    /// <summary>Shut down and reap the runtime subprocess. Idempotent and terminal — a closed harness no longer retries a failed handshake.</summary>
    /// <returns>settlement of the complete teardown.</returns>
    public Task CloseAsync()
    {
        _closed = true;
        return _client.CloseAsync();
    }

    /// <inheritdoc />
    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task StartCoreAsync()
    {
        try
        {
            _client.Start();
            await _client.InitializeAsync(new InitializeParams(_cwd, _provider, _model, _reasoningEffort, _maxTokens))
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _initialized = null;
            try
            {
                await _client.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("DeepSeek Harness initialization and cleanup failed", error, cleanupError);
            }
            if (!_closed) _client = _createClient();
            throw;
        }
    }
}

/// <summary>One SDK session: a stable id plus owned activity intervals.</summary>
public sealed class HarnessSession
{
    /// <summary>Create a session handle over one harness.</summary>
    /// <param name="harness">the owning harness (supplies the client and handshake).</param>
    /// <param name="id">the wire session id this handle runs on.</param>
    public HarnessSession(DeepSeekHarness harness, string id)
    {
        Harness = harness ?? throw new ArgumentNullException(nameof(harness));
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    /// <summary>The owning harness (supplies the client and handshake).</summary>
    public DeepSeekHarness Harness { get; }

    /// <summary>The wire session id this handle runs on.</summary>
    public string Id { get; }

    /// <summary>
    /// Queue one prompt, then observe the whole session through its next idle.
    /// </summary>
    /// <param name="input">the prompt text.</param>
    /// <param name="options">optional per-notification observer.</param>
    /// <returns>the owned activity interval; rejects on transport loss, timeout, or a protocol error.</returns>
    public async Task<RunResult> RunAsync(string input, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        await Harness.StartAsync().ConfigureAwait(false);
        var client = Harness.Client;
        var contentBlocks = SdkWire.NormalizeInput(input);
        var events = new List<WireSessionEvent>();
        var notifications = new List<HarnessNotification>();
        using var subscription = client.SubscribeSessionTree(Id);
        try
        {
            var messageId = await client.PromptAsync(Id, contentBlocks).ConfigureAwait(false);
            var received = false;
            while (true)
            {
                var notification = await subscription.NextAsync().ConfigureAwait(false);
                if (!received)
                {
                    // The interval starts at the durable enqueue receipt; the response only
                    // confirms admission.
                    if (notification.Method != SdkProtocol.SessionEvent
                        || !SdkWire.SessionMatches(notification, Id)
                        || !SdkWire.IsInboxReceipt(notification.Params, messageId))
                    {
                        continue;
                    }
                    received = true;
                }
                notifications.Add(notification);
                options?.OnNotification?.Invoke(notification);
                if (notification.Method == SdkProtocol.SessionEvent && SdkWire.SessionMatches(notification, Id))
                {
                    // Wire boundary: the envelope feeds the typed RunResult, so a malformed runtime
                    // surfaces as a protocol error, not as type-invalid data.
                    events.Add(SdkWire.ValidatedSessionEvent(notification.Params));
                }
                if (notification.Method == SdkProtocol.SessionStatus
                    && SdkWire.SessionMatches(notification, Id)
                    && SdkWire.IsIdle(notification))
                {
                    break;
                }
            }
        }
        finally
        {
            subscription.Dispose();
        }
        return new RunResult(Id, SdkWire.FinalResponse(events), events, notifications);
    }
}
