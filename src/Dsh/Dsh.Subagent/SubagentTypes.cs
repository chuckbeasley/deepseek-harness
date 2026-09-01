namespace Dsh.Subagent;

/// <summary>Registry-minted subagent identity.</summary>
public readonly record struct SubagentId(string Value)
{
    public static implicit operator string(SubagentId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>Lifecycle state of one delegated subagent.</summary>
public enum SubagentStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Why a delegated run ended (port of the TS <c>SubagentStopReason</c> union). Merge-extensible:
/// consumers switch on the known values and treat anything else as an error.
/// </summary>
public enum SubagentStopReason
{
    Completed,
    Aborted,
    Error,
    MaxTokens,
    Refusal,
}

/// <summary>
/// A caller's delegation request: the task text, an optional display label, and the parent
/// session facts the in-process driver needs to spawn the child loop (its session ancestry,
/// the delegation depth, and the provider route the recorded children replay under).
/// </summary>
public sealed record SubagentRequest(
    string Task,
    string? Label = null,
    string? ParentSessionId = null,
    int? ParentDelegationDepth = null,
    string? Provider = null,
    string? Model = null);

/// <summary>
/// The settled result of one delegation. <see cref="IsError"/> derives from
/// <see cref="StopReason"/>: only <see cref="SubagentStopReason.Completed"/> is a success.
/// </summary>
public sealed record SubagentResult(
    /// <summary>The selected final output text, empty when the child produced none.</summary>
    string Text,
    /// <summary>Provider-authored safe failure facts (capped, never raw error text or env values).</summary>
    string? Diagnostic = null,
    /// <summary>Why the run ended.</summary>
    SubagentStopReason StopReason = SubagentStopReason.Completed)
{
    /// <summary>Whether the run ended abnormally.</summary>
    public bool IsError => StopReason != SubagentStopReason.Completed;
}

/// <summary>Stable machine code for subagent failures; UI layers branch on the code, not messages.</summary>
public sealed class SubagentError : Exception
{
    /// <summary>Create the failure with its stable code.</summary>
    public SubagentError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Create the failure with its stable code and the underlying cause.</summary>
    public SubagentError(string message, string code, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (for example <c>NO_PROVIDER</c>, <c>DUPLICATE_PROVIDER</c>).</summary>
    public string Code { get; }
}

/// <summary>Start-time feature flags one provider supports (port of the TS <c>SubagentCapabilities</c>).</summary>
public sealed record SubagentCapabilities(
    bool AgentOptions,
    bool OutputSchema,
    bool DepthLimit,
    bool ToolFilter,
    bool Persona)
{
    /// <summary>The out-of-process baseline: every start-time flag unsupported.</summary>
    public static SubagentCapabilities None { get; } = new(false, false, false, false, false);
}

/// <summary>
/// A live delegation handle: the only access path to a running subagent's lifecycle, result, and
/// cancellation. Settles exactly once.
/// </summary>
public interface ISubagentHandle
{
    /// <summary>The delegation identity.</summary>
    SubagentId Id { get; }

    /// <summary>The current lifecycle state.</summary>
    SubagentStatus Status { get; }

    /// <summary>Resolves at settlement with the result (never rejects; a failed body settles Failed with the error text).</summary>
    Task<SubagentResult> Done { get; }

    /// <summary>Cancel the delegation (idempotent; false once settled).</summary>
    bool Cancel();
}

/// <summary>
/// One published out-of-process run (port of the TS <c>SubagentRun</c>): the result promise never
/// rejects after publication, and disposal is idempotent.
/// </summary>
public interface ISubagentRun
{
    /// <summary>The run identity, minted in the parent namespace.</summary>
    SubagentId Id { get; }

    /// <summary>
    /// Resolves at settlement with the result. Never rejects after publication: a child-level
    /// failure settles <see cref="SubagentStopReason.Error"/> with safe diagnostic facts.
    /// </summary>
    Task<SubagentResult> Result { get; }

    /// <summary>
    /// Settle the run locally (aborted when still running) and tear the child down. Idempotent.
    /// </summary>
    Task DisposeAsync();
}

/// <summary>
/// One registered driver (port of the TS <c>SubagentProvider</c>): a named, capability-typed
/// delegator. The runtime routes <c>start</c> calls to the provider by name.
/// </summary>
public interface ISubagentProvider
{
    /// <summary>Unique registry name (for example <c>subagent</c>, <c>dsh-sdk</c>).</summary>
    string Name { get; }

    /// <summary>The start-time features this provider supports.</summary>
    SubagentCapabilities Capabilities { get; }

    /// <summary>Whether the child sees the parent's completed-turn prefix (descriptive only).</summary>
    bool InheritsParentContext { get; }

    /// <summary>Optional static provider-owned provider/model route for one-shot runs.</summary>
    (string Provider, string Model)? AgentRouteDefaults { get; }

    /// <summary>
    /// Start one delegated run.
    /// </summary>
    /// <param name="request">the delegation request.</param>
    /// <param name="cancellationToken">withdraws the run before publication; after publication it
    /// settles the run locally as aborted and tears the child down.</param>
    /// <returns>the published run handle.</returns>
    Task<ISubagentRun> StartAsync(SubagentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Service Definition of the subagent capability (ctx.subagent): delegate a task to a named
/// driver. The in-process driver runs task bodies on worker tasks; out-of-process drivers spawn
/// a child runtime. One driver per name: two drivers claiming the same name would make start
/// routing ambiguous.
/// </summary>
public interface ISubagentService
{
    /// <summary>
    /// Delegate one task to the in-process driver and return its handle immediately.
    /// </summary>
    ISubagentHandle Delegate(SubagentRequest request);

    /// <summary>
    /// Register one driver. The returned disposer withdraws it; a run already in flight for the
    /// driver is not cancelled (the run owns its child).
    /// </summary>
    /// <exception cref="SubagentError">code <c>DUPLICATE_PROVIDER</c> when the name is taken.</exception>
    IDisposable RegisterProvider(ISubagentProvider provider);

    /// <summary>One registered driver, or <c>null</c> when nothing claims the name.</summary>
    ISubagentProvider? GetProvider(string name);

    /// <summary>Every registered driver, in registration order.</summary>
    IReadOnlyList<ISubagentProvider> List();

    /// <summary>
    /// Start one delegated run through the named driver.
    /// </summary>
    /// <param name="name">the registered driver name.</param>
    /// <param name="request">the delegation request.</param>
    /// <param name="cancellationToken">withdraws the run before or after publication.</param>
    /// <returns>the published run handle.</returns>
    /// <exception cref="SubagentError">code <c>NO_PROVIDER</c> when nothing claims the name.</exception>
    Task<ISubagentRun> StartAsync(string name, SubagentRequest request, CancellationToken cancellationToken = default);
}
