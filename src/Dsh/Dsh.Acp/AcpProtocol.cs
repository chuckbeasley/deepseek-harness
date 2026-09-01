using System.Text.Json;

namespace Harness.Acp;

/// <summary>The ACP server's camelCase wire serializer (the transport embeds JsonElements verbatim).</summary>
internal static class AcpWire
{
    /// <summary>Options for every ACP wire read and write.</summary>
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

/// <summary>
/// Wire method names, the protocol version, and the wire-stable agent identity (the constants of
/// the <c>@agentclientprotocol/sdk</c> agent app).
/// </summary>
public static class AcpProtocol
{
    /// <summary>The wire protocol version this agent implements.</summary>
    public const string ProtocolVersion = "2025-03-26";

    /// <summary>Wire-stable agent identity name.</summary>
    public const string AgentName = "deepseek-harness-acp";

    /// <summary>Wire-stable agent identity version.</summary>
    public const string AgentVersion = "0.0.1";

    /// <summary>The process-wide ACP handshake request.</summary>
    public const string Initialize = "initialize";

    /// <summary>The auth handshake request (this agent exposes no auth methods).</summary>
    public const string Authenticate = "authenticate";

    /// <summary>Create one ACP-owned session.</summary>
    public const string SessionNew = "session/new";

    /// <summary>Page the persisted sessions.</summary>
    public const string SessionList = "session/list";

    /// <summary>Restore one persisted session.</summary>
    public const string SessionResume = "session/resume";

    /// <summary>Close one ACP-owned session.</summary>
    public const string SessionClose = "session/close";

    /// <summary>Apply one standard configuration option.</summary>
    public const string SetSessionConfigOption = "session/setConfigOption";

    /// <summary>Queue one prompt and settle at the correlated turn's end.</summary>
    public const string SessionPrompt = "session/prompt";

    /// <summary>Client-to-agent notification: cancel the active prompt (or autonomous work).</summary>
    public const string SessionCancel = "session/cancel";

    /// <summary>Agent-to-client notification: one committed session update.</summary>
    public const string ClientSessionUpdate = "session/update";

    /// <summary>Agent-to-client request: one one-shot permission decision.</summary>
    public const string ClientRequestPermission = "session/requestPermission";
}

/// <summary>Parameters for <c>session/new</c> (the optional arrays stay raw for validation).</summary>
public sealed record NewSessionParams(
    /// <summary>The canonical primary workspace (must be absolute).</summary>
    string Cwd,
    /// <summary>Standard MCP server declarations; non-empty is refused (the port's MCP reduction).</summary>
    JsonElement? McpServers = null,
    /// <summary>Additional workspace directories; non-empty is refused.</summary>
    JsonElement? AdditionalDirectories = null);

/// <summary>Parameters for <c>session/resume</c>.</summary>
public sealed record ResumeSessionParams(
    /// <summary>The persisted session id.</summary>
    string SessionId,
    /// <summary>The canonical primary workspace (must be absolute).</summary>
    string Cwd,
    /// <summary>Standard MCP server declarations; non-empty is refused (the port's MCP reduction).</summary>
    JsonElement? McpServers = null);

/// <summary>Parameters for <c>session/list</c>.</summary>
public sealed record ListSessionsParams(
    /// <summary>Optional workspace filter (validated; the port's persisted headers carry no cwd, so the filter is vacuous).</summary>
    string? Cwd = null,
    /// <summary>Opaque continuation token from a previous page.</summary>
    string? Cursor = null);

/// <summary>Parameters for <c>session/setConfigOption</c>.</summary>
public sealed record SetConfigOptionParams(
    /// <summary>The ACP-owned session id.</summary>
    string SessionId,
    /// <summary>The advertised standard option id.</summary>
    string ConfigId,
    /// <summary>The opaque selected value returned by a previous option state.</summary>
    JsonElement Value);

/// <summary>Parameters for <c>session/close</c>.</summary>
public sealed record CloseSessionParams(
    /// <summary>The ACP-owned session id.</summary>
    string SessionId);

/// <summary>Parameters for <c>session/prompt</c>.</summary>
public sealed record PromptParams(
    /// <summary>The ACP-owned session id.</summary>
    string SessionId,
    /// <summary>The raw prompt content-block array (validated at admission).</summary>
    JsonElement Prompt);

/// <summary>Result of <c>session/prompt</c>: the correlated standard stop reason.</summary>
public sealed record PromptResult(
    /// <summary>The stop reason after ordered updates drain (<c>end_turn</c>, <c>max_tokens</c>, or <c>cancelled</c>).</summary>
    string StopReason);

/// <summary>One standard select choice of a session configuration option.</summary>
public sealed record SessionConfigChoice(
    /// <summary>The opaque selector value.</summary>
    string Value,
    /// <summary>The human-readable choice name.</summary>
    string Name,
    /// <summary>Optional choice description.</summary>
    string? Description = null);

/// <summary>One provider group of a session configuration option.</summary>
public sealed record SessionConfigGroup(
    /// <summary>The provider route id.</summary>
    string Group,
    /// <summary>The human-readable provider name.</summary>
    string Name,
    /// <summary>The group's model choices.</summary>
    IReadOnlyList<SessionConfigChoice> Options);

/// <summary>One standard session configuration option.</summary>
public sealed record SessionConfigOption(
    /// <summary>The standard option id.</summary>
    string Id,
    /// <summary>The human-readable option name.</summary>
    string Name,
    /// <summary>The standard option category.</summary>
    string Category,
    /// <summary>The option kind (always <c>select</c>).</summary>
    string Type,
    /// <summary>The current opaque value.</summary>
    string CurrentValue,
    /// <summary>The grouped choices.</summary>
    IReadOnlyList<SessionConfigGroup> Options);

/// <summary>One committed <c>session/update</c> payload (the standard union subset this bridge emits).</summary>
public abstract record SessionUpdate
{
    /// <summary>The wire discriminator.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
    public abstract string Kind { get; }
}

/// <summary>One assembled assistant text block.</summary>
public sealed record AgentMessageChunkUpdate(
    /// <summary>The assistant message id.</summary>
    string MessageId,
    /// <summary>The wire content object (a text block).</summary>
    JsonElement Content) : SessionUpdate
{
    /// <inheritdoc />
    [System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
    public override string Kind => "agent_message_chunk";
}

/// <summary>One assembled assistant reasoning block.</summary>
public sealed record AgentThoughtChunkUpdate(
    /// <summary>The assistant message id.</summary>
    string MessageId,
    /// <summary>The wire content object (a text block).</summary>
    JsonElement Content) : SessionUpdate
{
    /// <inheritdoc />
    [System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
    public override string Kind => "agent_thought_chunk";
}

/// <summary>One generic tool lifecycle start.</summary>
public sealed record ToolCallUpdate(
    /// <summary>The tool call id.</summary>
    string ToolCallId,
    /// <summary>The tool title.</summary>
    string Title,
    /// <summary>The tool kind (always <c>other</c>; wire name <c>kind</c>).</summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string ToolKind,
    /// <summary>The lifecycle status (always <c>in_progress</c>).</summary>
    string Status,
    /// <summary>The raw parsed arguments, or the raw string when unparsable.</summary>
    JsonElement RawInput) : SessionUpdate
{
    /// <inheritdoc />
    [System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
    public override string Kind => "tool_call";
}

/// <summary>One generic tool lifecycle finish.</summary>
public sealed record ToolCallUpdateResult(
    /// <summary>The tool call id.</summary>
    string ToolCallId,
    /// <summary>The lifecycle status (<c>completed</c> or <c>failed</c>).</summary>
    string Status,
    /// <summary>The ordered wire content items.</summary>
    IReadOnlyList<JsonElement> Content) : SessionUpdate
{
    /// <inheritdoc />
    [System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
    public override string Kind => "tool_call_update";
}

/// <summary>The standard option state after a topology or mutation change.</summary>
public sealed record ConfigOptionUpdate(
    /// <summary>The complete resulting option state.</summary>
    IReadOnlyList<SessionConfigOption> ConfigOptions) : SessionUpdate
{
    /// <inheritdoc />
    [System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
    public override string Kind => "config_option_update";
}
