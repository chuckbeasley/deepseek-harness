using Harness.Cordis.Core;

namespace Harness.Subagent;

/// <summary>
/// The subagent runtime (ctx.subagent): the in-process driver plus the provider registry the
/// out-of-process drivers register into. The in-process driver runs each delegation's task body
/// on a worker task with a fresh cancellation; teardown cancels and awaits every live delegation.
/// </summary>
public sealed class InProcessSubagentProvider : Service, ISubagentService
{
    private readonly object _gate = new();
    private readonly List<LocalHandle> _live = new();
    private readonly Dictionary<string, ISubagentProvider> _providers = new(StringComparer.Ordinal);
    private readonly Func<SubagentRequest, CancellationToken, Task<SubagentResult>> _runner;
    private int _counter;

    /// <summary>
    /// Create the runtime, register it as <c>subagent</c>, and register the in-process driver as
    /// the provider named <c>subagent</c>.
    /// </summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <param name="runner">the in-process task body; returns the settled delegation result (a non-completed stop reason fails the delegation).</param>
    public InProcessSubagentProvider(Context ctx, Func<SubagentRequest, CancellationToken, Task<SubagentResult>>? runner = null)
        : base(ctx, "subagent")
    {
        _runner = runner ?? DefaultRunner;
        _providers.Add("subagent", new InProcessProviderAdapter(this));
    }

    /// <inheritdoc />
    public ISubagentHandle Delegate(SubagentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Task.Trim().Length == 0)
        {
            throw new ArgumentException("subagent: the task must be a non-empty string", nameof(request));
        }
        int id;
        lock (_gate) id = ++_counter;
        var handle = new LocalHandle(new SubagentId($"subagent-{id}"), request, _runner);
        lock (_gate) _live.Add(handle);
        _ = handle.Done.ContinueWith(_ =>
        {
            lock (_gate) _live.Remove(handle);
        }, TaskScheduler.Default);
        handle.Start();
        return handle;
    }

    /// <inheritdoc />
    public IDisposable RegisterProvider(ISubagentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Name.Trim().Length == 0)
        {
            throw new ArgumentException("subagent: a provider name must be non-empty", nameof(provider));
        }
        lock (_gate)
        {
            if (_providers.ContainsKey(provider.Name))
            {
                throw new SubagentError($"a subagent provider named \"{provider.Name}\" is already registered", "DUPLICATE_PROVIDER");
            }
            _providers.Add(provider.Name, provider);
        }
        return new ActionDisposer(() =>
        {
            lock (_gate) _providers.Remove(provider.Name);
        });
    }

    /// <inheritdoc />
    public ISubagentProvider? GetProvider(string name)
    {
        lock (_gate) return _providers.TryGetValue(name, out var provider) ? provider : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ISubagentProvider> List()
    {
        lock (_gate) return _providers.Values.ToArray();
    }

    /// <inheritdoc />
    public async Task<ISubagentRun> StartAsync(string name, SubagentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ISubagentProvider provider;
        lock (_gate)
        {
            if (!_providers.TryGetValue(name, out var found))
            {
                throw new SubagentError($"no subagent provider is registered for \"{name}\"", "NO_PROVIDER");
            }
            provider = found;
        }
        return await provider.StartAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Teardown: cancel and await every live delegation, then drop the registered drivers.</summary>
    public override async ValueTask StopAsync()
    {
        LocalHandle[] live;
        lock (_gate) live = _live.ToArray();
        foreach (var handle in live) handle.Cancel();
        foreach (var handle in live)
        {
            try
            {
                await handle.Done;
            }
            catch
            {
                // Settled as Failed with the error text inside the handle.
            }
        }
        await base.StopAsync();
    }

    private static Task<SubagentResult> DefaultRunner(SubagentRequest request, CancellationToken ct)
        => throw new InvalidOperationException("subagent: no runner is mounted — supply one to InProcessSubagentProvider");

    /// <summary>One live delegation: state, cancellation, and the settlement promise.</summary>
    private sealed class LocalHandle : ISubagentHandle
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<SubagentResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<SubagentRequest, CancellationToken, Task<SubagentResult>> _runner;
        private int _status = (int)SubagentStatus.Running;
        private int _settled;

        public LocalHandle(SubagentId id, SubagentRequest request, Func<SubagentRequest, CancellationToken, Task<SubagentResult>> runner)
        {
            Id = id;
            Request = request;
            _runner = runner;
        }

        public SubagentId Id { get; }

        public SubagentRequest Request { get; }

        public SubagentStatus Status => (SubagentStatus)Volatile.Read(ref _status);

        public Task<SubagentResult> Done => _done.Task;

        public bool Cancel()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            _cts.Cancel();
            return true;
        }

        public void Start()
        {
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                var result = await _runner(Request, _cts.Token);
                Volatile.Write(ref _status, result.StopReason == SubagentStopReason.Completed
                    ? (int)SubagentStatus.Completed
                    : (int)SubagentStatus.Failed);
                _done.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _status, (int)SubagentStatus.Cancelled);
                _done.TrySetResult(new SubagentResult("delegation cancelled", StopReason: SubagentStopReason.Aborted));
            }
            catch (Exception error)
            {
                Volatile.Write(ref _status, (int)SubagentStatus.Failed);
                _done.TrySetResult(new SubagentResult(error.Message, StopReason: SubagentStopReason.Error));
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    /// <summary>The in-process driver as a named provider: one published run per delegation.</summary>
    private sealed class InProcessProviderAdapter : ISubagentProvider
    {
        private readonly InProcessSubagentProvider _service;

        public InProcessProviderAdapter(InProcessSubagentProvider service)
        {
            _service = service;
        }

        public string Name => "subagent";

        public SubagentCapabilities Capabilities => SubagentCapabilities.None;

        public bool InheritsParentContext => false;

        public (string Provider, string Model)? AgentRouteDefaults => null;

        public Task<ISubagentRun> StartAsync(SubagentRequest request, CancellationToken cancellationToken)
        {
            var handle = _service.Delegate(request);
            var withdrawal = cancellationToken.Register(() => handle.Cancel());
            return Task.FromResult<ISubagentRun>(new HandleRun(handle, withdrawal));
        }

        private sealed class HandleRun : ISubagentRun
        {
            private readonly ISubagentHandle _handle;
            private readonly CancellationTokenRegistration _withdrawal;

            public HandleRun(ISubagentHandle handle, CancellationTokenRegistration withdrawal)
            {
                _handle = handle;
                _withdrawal = withdrawal;
            }

            public SubagentId Id => _handle.Id;

            public Task<SubagentResult> Result => _handle.Done;

            public Task DisposeAsync()
            {
                _withdrawal.Dispose();
                _handle.Cancel();
                return Task.CompletedTask;
            }
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
