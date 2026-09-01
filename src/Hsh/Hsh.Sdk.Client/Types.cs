using System.Text.Json;
using Harness.Sdk.Protocol;

namespace Harness.Sdk.Client;

/// <summary>One server-to-client notification as received off the wire.</summary>
public sealed record HarnessNotification(
    /// <summary>The JSON-RPC notification method name.</summary>
    string Method,
    /// <summary>The raw params object (absent params arrive as <c>{}</c>).</summary>
    JsonElement Params);

/// <summary>Predicate deciding whether a subscription receives a notification.</summary>
public delegate bool NotificationFilter(HarnessNotification notification);

/// <summary>Launch and timeout options for <see cref="HarnessClient"/> (port of the TS <c>HarnessClientOptions</c>).</summary>
public class HarnessClientOptions
{
    /// <summary>The child runtime entry: a .dll spawned via <c>dotnet</c>, or an apphost executable;
    /// omitted resolves the current executable (the hsh CLI when hosted in a hsh surface).</summary>
    public string? HshBin { get; set; }

    /// <summary>Named profile serving the SDK protocol (default <c>sdk</c>).</summary>
    public string? Profile { get; set; }

    /// <summary>Ordered per-launch profile patches; relative paths resolve before spawn.</summary>
    public IReadOnlyList<string>? Patches { get; set; }

    /// <summary>Explicit Harness home for this child; relative paths resolve before spawn.</summary>
    public string? HshHome { get; set; }

    /// <summary>Working directory for the hsh process itself.</summary>
    public string? ProcessCwd { get; set; }

    /// <summary>
    /// The complete child environment, materialized when the client starts. <c>null</c> reads the
    /// parent environment at that time; a provided dictionary replaces the parent environment
    /// entirely, so callers own credential policy.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; set; }

    /// <summary>Bound (ms) on the initial profile handshake (default 10000).</summary>
    public int? InitializeTimeoutMs { get; set; }

    /// <summary>Per-request timeout (ms); <c>null</c> waits indefinitely (a turn can legitimately run long).</summary>
    public int? RequestTimeoutMs { get; set; }

    /// <summary>Bound (ms) on the protocol <c>shutdown</c> exchange inside <see cref="HarnessClient.CloseAsync"/> (default 1000).</summary>
    public int? ShutdownTimeoutMs { get; set; }

    /// <summary>Grace (ms) for the runtime's stdin-EOF quiesce during close (default 6000).</summary>
    public int? DisposeEofGraceMs { get; set; }

    /// <summary>Termination confirmation window (ms) after the forced kill during close (default 3000).</summary>
    public int? DisposeGraceMs { get; set; }
}

/// <summary>Options for the high-level <see cref="DeepSeekHarness"/> wrapper (the TS <c>DeepSeekHarnessOptions</c>).</summary>
public class DeepSeekHarnessOptions : HarnessClientOptions
{
    /// <summary>Workspace cwd recorded on every SDK-created session (default: the process cwd).</summary>
    public string? Cwd { get; set; }

    /// <summary>Provider route for SDK-created agents (default <c>deepseek-official</c>).</summary>
    public string? Provider { get; set; }

    /// <summary>Model for SDK-created agents (default <c>deepseek-v4-flash</c>).</summary>
    public string? Model { get; set; }

    /// <summary>Adapter-owned reasoning effort for the selected provider/model route (documented
    /// reduction: the ported AgentOptions has no reasoning-effort seat).</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Maximum output tokens for each conversation-model request.</summary>
    public int? MaxTokens { get; set; }
}

/// <summary>Per-run options: target session and streaming observer.</summary>
public sealed record RunOptions(
    /// <summary>Session id to run on; omitted mints a fresh session per call.</summary>
    string? SessionId = null,
    /// <summary>Observer invoked with every notification for this session tree, in wire order.</summary>
    Action<HarnessNotification>? OnNotification = null);

/// <summary>One owned session activity interval, from enqueue receipt through idle.</summary>
public sealed record RunResult(
    /// <summary>The session the activity ran on.</summary>
    string SessionId,
    /// <summary>Concatenated text of the interval's last assistant message (empty when none).</summary>
    string FinalResponse,
    /// <summary>Every <c>session.event</c> envelope for the root session, in wire order.</summary>
    IReadOnlyList<WireSessionEvent> Events,
    /// <summary>Every notification for the root session and discovered descendants, in wire order.</summary>
    IReadOnlyList<HarnessNotification> Notifications);
