namespace Dsh.Subagent;

/// <summary>
/// One already-settled run handle the deterministic fixture providers return.
/// </summary>
internal sealed class SettledRun(SubagentId id, SubagentResult result) : ISubagentRun
{
    public SubagentId Id { get; } = id;

    public Task<SubagentResult> Result { get; } = Task.FromResult(result);

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The recorded product-shaped diagnostic provider (the port of the snapshot
/// <c>subagent-result-diagnostic</c> fixture): exactly four starts answering the recorded
/// Claude Code and Codex failures with their partial outputs, resolving instantly so a
/// background job settles inline and its completion notice reaches the owning agent.
/// </summary>
public sealed class DiagnosticSnapshotProvider : ISubagentProvider
{
    private static readonly (string Text, string Diagnostic)[] Results =
    {
        ("partial assistant text", "Product subagent failure (product: Claude Code; stage: query-run; category: limit)"),
        ("", "Product subagent failure (product: Claude Code; stage: query-run; category: limit)"),
        ("partial assistant text", "Product subagent failure (product: Codex; stage: turn; category: transport; HTTP status: 503)"),
        ("", "Product subagent failure (product: Codex; stage: turn; category: transport; HTTP status: 503)"),
    };

    private int _starts;

    /// <inheritdoc />
    public string Name => "snapshot-diagnostic";

    /// <inheritdoc />
    public SubagentCapabilities Capabilities => SubagentCapabilities.None;

    /// <inheritdoc />
    public bool InheritsParentContext => false;

    /// <inheritdoc />
    public (string Provider, string Model)? AgentRouteDefaults => null;

    /// <inheritdoc />
    public Task<ISubagentRun> StartAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        var index = _starts++;
        if (index >= Results.Length)
        {
            throw new InvalidOperationException("snapshot diagnostic provider expected exactly four starts");
        }
        var result = Results[index];
        var id = new SubagentId($"00000000-0000-4000-8000-{index + 1:x12}");
        return Task.FromResult<ISubagentRun>(new SettledRun(
            id,
            new SubagentResult(result.Text, result.Diagnostic, SubagentStopReason.Error)));
    }
}

/// <summary>
/// The recorded ACP mock provider (the port of the snapshot <c>mock-acp-server</c>): every
/// delegation requests an execute-tool permission that the reject policy denies, so the run
/// aborts with the recorded unattended-decision diagnostic. Settlement is deferred past the
/// spawning step so a background job settles under a pending <c>job_output</c> wait (which
/// reports it and suppresses the completion notice) exactly like the recorded transcript.
/// </summary>
public sealed class AcpSnapshotProvider : ISubagentProvider
{
    /// <summary>How long a delegation waits before settling (the ACP permission round-trip stand-in).</summary>
    public const int SettlementDelayMs = 150;

    /// <inheritdoc />
    public string Name => "acp-diagnostic";

    /// <inheritdoc />
    public SubagentCapabilities Capabilities => SubagentCapabilities.None;

    /// <inheritdoc />
    public bool InheritsParentContext => false;

    /// <inheritdoc />
    public (string Provider, string Model)? AgentRouteDefaults => null;

    /// <inheritdoc />
    public Task<ISubagentRun> StartAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        var id = new SubagentId($"00000000-0000-4000-8000-0000000000d{Math.Max(1, _starts++)}");
        var result = new SubagentResult(
            string.Empty,
            "ACP unattended decision (policy: reject; request: execute; decision: denied)",
            SubagentStopReason.Aborted);
        return Task.FromResult<ISubagentRun>(new DelayedRun(id, result, cancellationToken));
    }

    private int _starts;

    /// <summary>One run whose result settles after the recorded permission round-trip delay.</summary>
    private sealed class DelayedRun(SubagentId id, SubagentResult result, CancellationToken cancellationToken) : ISubagentRun
    {
        public SubagentId Id { get; } = id;

        public Task<SubagentResult> Result { get; } = SettleAsync(result, cancellationToken);

        public Task DisposeAsync() => Task.CompletedTask;

        private static async Task<SubagentResult> SettleAsync(SubagentResult result, CancellationToken ct)
        {
            await Task.Delay(SettlementDelayMs, ct).ConfigureAwait(false);
            return result;
        }
    }
}