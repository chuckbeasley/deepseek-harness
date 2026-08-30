using Cordis.Core;

namespace Dsh.Subagent;

/// <summary>
/// In-process subagent provider (ctx.subagent; the in-process driver half of the TS subagent seam —
/// the out-of-process drivers, child-agent composition, and control/report tools arrive with a
/// later wave). Each delegation runs its task body on a worker task with a fresh cancellation;
/// teardown cancels and awaits every live delegation.
/// </summary>
public sealed class InProcessSubagentProvider : Service, ISubagentService
{
    private readonly object _gate = new();
    private readonly List<LocalHandle> _live = new();
    private readonly Func<SubagentRequest, CancellationToken, Task<string>> _runner;
    private int _counter;

    /// <summary>
    /// Create the provider and register it as <c>subagent</c>.
    /// </summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <param name="runner">the task body; returns the final text (throws to fail the delegation).</param>
    public InProcessSubagentProvider(Context ctx, Func<SubagentRequest, CancellationToken, Task<string>>? runner = null)
        : base(ctx, "subagent")
    {
        _runner = runner ?? DefaultRunner;
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

    /// <summary>Teardown: cancel and await every live delegation.</summary>
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

    private static Task<string> DefaultRunner(SubagentRequest request, CancellationToken ct)
        => throw new InvalidOperationException("subagent: no runner is mounted — supply one to InProcessSubagentProvider");

    /// <summary>One live delegation: state, cancellation, and the settlement promise.</summary>
    private sealed class LocalHandle : ISubagentHandle
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<SubagentResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<SubagentRequest, CancellationToken, Task<string>> _runner;
        private int _status = (int)SubagentStatus.Running;
        private int _settled;

        public LocalHandle(SubagentId id, SubagentRequest request, Func<SubagentRequest, CancellationToken, Task<string>> runner)
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
                var text = await _runner(Request, _cts.Token);
                Volatile.Write(ref _status, (int)SubagentStatus.Completed);
                _done.TrySetResult(new SubagentResult(text, IsError: false));
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _status, (int)SubagentStatus.Cancelled);
                _done.TrySetResult(new SubagentResult("delegation cancelled", IsError: false));
            }
            catch (Exception error)
            {
                Volatile.Write(ref _status, (int)SubagentStatus.Failed);
                _done.TrySetResult(new SubagentResult(error.Message, IsError: true));
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
