using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>Where a message came from. Merge-extensible sum type; plugins add their own kinds.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserSource), "user")]
[JsonDerivedType(typeof(PluginSource), "plugin")]
[JsonDerivedType(typeof(ModelSource), "model")]
[JsonDerivedType(typeof(ToolSource), "tool")]
public abstract record MessageSource
{
    /// <summary>Who produced this message (read-only discriminant).</summary>
    [JsonIgnore]
    public abstract string Kind { get; }
}

/// <summary>A direct human prompt.</summary>
public sealed record UserSource : MessageSource
{
    [JsonIgnore]
    public override string Kind => "user";
}

/// <summary>Plugin-injected content (e.g. the assembler default).</summary>
public sealed record PluginSource : MessageSource
{
    /// <summary>The contributing plugin's name.</summary>
    public required string Plugin { get; init; }

    /// <summary>Optional form of the contribution (e.g. "snapshot" for runtime-context projections).</summary>
    public string? Form { get; init; }

    /// <summary>Optional named contributions that formed a snapshot message.</summary>
    public IReadOnlyList<string>? Sections { get; init; }

    [JsonIgnore]
    public override string Kind => "plugin";
}

/// <summary>A model-produced message: provider route and model id.</summary>
public sealed record ModelSource : MessageSource
{
    /// <summary>Provider route that produced the message.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider model id that produced the message.</summary>
    public required string Model { get; init; }

    [JsonIgnore]
    public override string Kind => "model";
}

/// <summary>A user-role message carrying one tool result.</summary>
public sealed record ToolSource : MessageSource
{
    /// <summary>The call id the result answers.</summary>
    public required ToolCallId CallId { get; init; }

    [JsonIgnore]
    public override string Kind => "tool";
}

/// <summary>
/// One immutable message representation shared by delivery, durable history, and model requests.
/// The read-only computed <see cref="Role"/> discriminant serializes but is skipped on read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "tool-result")]
public abstract record Message
{
    /// <summary>Stable identity preserved across every representation boundary.</summary>
    [JsonPropertyOrder(0)]
    public required MessageId Id { get; init; }

    /// <summary>Provider-neutral conversation role.</summary>
    [JsonPropertyOrder(1)]
    public abstract string Role { get; }

    /// <summary>Exact model-facing blocks.</summary>
    [JsonPropertyOrder(2)]
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>Required source fields supplied by the producer.</summary>
    [JsonPropertyOrder(3)]
    public required MessageSource Source { get; init; }
}

/// <summary>A user-role specialization of the one shared message representation.</summary>
public sealed record UserMessage : Message
{
    [JsonPropertyOrder(1)]
    public override string Role => "user";
}

/// <summary>A model-produced assistant specialization of the shared message representation.</summary>
public sealed record AssistantMessage : Message
{
    [JsonPropertyOrder(1)]
    public override string Role => "assistant";
}

/// <summary>
/// A tool-result specialization whose model-facing content is exactly one tool-result block and
/// whose source is the matching tool source. Use <see cref="Create"/> to guarantee the pairing.
/// </summary>
public sealed record ToolResultMessage : Message
{
    [JsonPropertyOrder(1)]
    public override string Role => "user";

    /// <summary>The single tool-result block (convenience view of <see cref="Message.Content"/>).</summary>
    [JsonIgnore]
    public ToolResultBlock Result => (ToolResultBlock)Content[0];

    /// <summary>Create one identified tool-result message with a freshly minted id.</summary>
    public static ToolResultMessage Create(ToolCallId callId, IReadOnlyList<ContentBlock> content, bool isError = false)
        => new()
        {
            Id = new MessageId(Guid.NewGuid().ToString("N")),
            Content = new ContentBlock[] { new ToolResultBlock(callId, content, isError) },
            Source = new ToolSource { CallId = callId },
        };
}

/// <summary>Immutable message construction helpers (the createMessage family).</summary>
public static class Messages
{
    /// <summary>Create one identified user-role message.</summary>
    public static UserMessage CreateUserMessage(IReadOnlyList<ContentBlock> content, MessageSource? source = null)
        => new()
        {
            Id = new MessageId(Guid.NewGuid().ToString("N")),
            Content = content,
            Source = source ?? new UserSource(),
        };

    /// <summary>Create one identified model-produced assistant message.</summary>
    public static AssistantMessage CreateAssistantMessage(string provider, string model, IReadOnlyList<ContentBlock> content)
        => new()
        {
            Id = new MessageId(Guid.NewGuid().ToString("N")),
            Content = content,
            Source = new ModelSource { Provider = provider, Model = model },
        };
}







