namespace Dsh.Sdk.Protocol;

/// <summary>Wire method names and the wire-stable server identity (the TS constants).</summary>
public static class SdkProtocol
{
    /// <summary>Wire-stable server identity name.</summary>
    public const string ServerName = "deepseek-harness-sdk-runtime";

    /// <summary>The process-wide SDK handshake request.</summary>
    public const string Initialize = "initialize";

    /// <summary>One user turn on one SDK session.</summary>
    public const string SessionPrompt = "session/prompt";

    /// <summary>The process-wide shutdown request.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>Server-to-client notification: one session-log event.</summary>
    public const string SessionEvent = "session.event";

    /// <summary>Server-to-client notification: whole-agent lifecycle state.</summary>
    public const string SessionStatus = "session.status";

    /// <summary>Server-to-client notification: an in-runtime child session was created.</summary>
    public const string SubagentStarted = "subagent.started";

    /// <summary>Server-to-client notification: an in-process subagent run ended.</summary>
    public const string SubagentFinished = "subagent.finished";
}

/// <summary>Parameters for the process-wide SDK handshake.</summary>
public sealed record InitializeParams(
    /// <summary>Working directory recorded on every SDK-created session's header.</summary>
    string Cwd,
    /// <summary>Provider route every SDK-created agent runs on.</summary>
    string Provider,
    /// <summary>Model name every SDK-created agent runs on.</summary>
    string Model,
    /// <summary>Optional adapter-owned reasoning effort for the selected route.</summary>
    string? ReasoningEffort = null,
    /// <summary>Optional positive output-token cap inherited by SDK-created agents.</summary>
    int? MaxTokens = null);

/// <summary>The wire-stable server identity (name and version).</summary>
public sealed record ServerInfo(string Name, string Version);

/// <summary>Wire-stable server identity returned by initialization.</summary>
public sealed record InitializeResult([property: System.Text.Json.Serialization.JsonPropertyName("serverInfo")] ServerInfo Info);

/// <summary>One user turn on one SDK session.</summary>
public sealed record SessionPromptParams(
    /// <summary>The SDK-side session id; an unknown id lazily creates the agent+session pair.</summary>
    string SessionId,
    /// <summary>The prompt content blocks, sent verbatim as the user message.</summary>
    IReadOnlyList<SdkPromptContentBlock> ContentBlocks);

/// <summary>Durable enqueue receipt for one prompt.</summary>
public sealed record SessionPromptResult(
    /// <summary>Identity of the queued user message.</summary>
    string MessageId);

/// <summary>Inline raster input admitted into the runtime's durable attachment store.</summary>
public sealed record SdkEncodedImageBlock(
    /// <summary>Canonical base64-encoded raster bytes.</summary>
    string Data,
    /// <summary>Declared raster MIME type, verified during admission.</summary>
    string MimeType);

/// <summary>SDK prompt input: an ordinary durable content block or an inline image awaiting admission.</summary>
public abstract record SdkPromptContentBlock
{
    /// <summary>One ordinary durable content block.</summary>
    public sealed record Block(Dsh.Llm.ContentBlock Content) : SdkPromptContentBlock;

    /// <summary>One inline raster image awaiting admission.</summary>
    public sealed record Image(SdkEncodedImageBlock Value) : SdkPromptContentBlock;
}

/// <summary>
/// The wire <c>session.event</c> envelope (the TS <c>SessionEvent</c> shape): the discriminator,
/// ordering fields, and the variant payload under <c>data</c>. The ported session records keep
/// their payload inline, so the server projects each record to this envelope on the wire.
/// </summary>
public sealed record WireSessionEvent(
    /// <summary>The event type discriminator (e.g. <c>user/message</c>).</summary>
    string Type,
    /// <summary>Monotonic sequence number within the session.</summary>
    long Seq,
    /// <summary>Unix epoch milliseconds.</summary>
    long TimeMs,
    /// <summary>The variant payload object (the record's fields minus the envelope).</summary>
    System.Text.Json.JsonElement Data);

/// <summary>Server-to-client notification: one session-log event, streamed as it is recorded.</summary>
public sealed record SessionEventNotification(
    /// <summary>Session the event belongs to (every session in the runtime, not only SDK-created ones).</summary>
    string SessionId,
    /// <summary>The full session-log event envelope.</summary>
    WireSessionEvent Event);

/// <summary>Server-to-client notification: whole-agent lifecycle state for one session.</summary>
public sealed record SessionStatusNotification(
    /// <summary>Session whose live agent changed status.</summary>
    string SessionId,
    /// <summary>The whole-agent state after the transition (<c>idle</c> or <c>running</c>).</summary>
    string Status);

/// <summary>Server-to-client notification: an in-runtime child session was created.</summary>
public sealed record SubagentStartedNotification(
    /// <summary>The delegating session.</summary>
    string ParentSessionId,
    /// <summary>The new child session.</summary>
    string ChildSessionId);

/// <summary>Server-to-client notification: an in-process subagent run ended (remote runs are not reported).</summary>
public sealed record SubagentFinishedNotification(
    /// <summary>Subagent provider name that ran the child.</summary>
    string Provider,
    /// <summary>The child agent's id (equals <c>ChildSessionId</c> for local runs).</summary>
    string AgentId,
    /// <summary>The delegating session.</summary>
    string ParentSessionId,
    /// <summary>The child session.</summary>
    string ChildSessionId,
    /// <summary>Deployment-mapped run outcome (<c>ok</c> or <c>error</c>).</summary>
    string Status,
    /// <summary>The provider-reported stop reason.</summary>
    Dsh.Subagent.SubagentStopReason StopReason,
    /// <summary>The child's selected assistant output; absent when the child produced none.</summary>
    IReadOnlyList<Dsh.Llm.ContentBlock>? LastAssistantMessage = null);

