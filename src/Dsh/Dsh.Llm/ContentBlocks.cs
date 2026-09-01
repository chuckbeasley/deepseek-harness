using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>Merge-extensible content blocks keyed by <see cref="BlockType"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
[JsonDerivedType(typeof(ToolCallBlock), "tool-call")]
[JsonDerivedType(typeof(ToolResultBlock), "tool-result")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract record ContentBlock
{
    /// <summary>The block <c>type</c> tag vocabulary (read-only; not part of the payload).</summary>
    public abstract string BlockType { get; }
}

/// <summary>One committed image attachment a model-facing result carries (the TS <c>ImageAttachmentRef</c> wire shape).</summary>
public sealed record ImageAttachment(
    [property: JsonPropertyName("attachmentId")] string AttachmentId,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("name")] string? Name = null);

/// <summary>An image block referencing a durably committed attachment.</summary>
public sealed record ImageBlock(ImageAttachment Attachment) : ContentBlock
{
    [JsonIgnore]
    public override string BlockType => "image";
}

/// <summary>Plain text visible to the end user.</summary>
public sealed record TextBlock(string Text) : ContentBlock
{
    [JsonIgnore]
    public override string BlockType => "text";
}

/// <summary>Reasoning / thinking content, distinct from visible text.</summary>
public sealed record ReasoningBlock(string Text) : ContentBlock
{
    [JsonIgnore]
    public override string BlockType => "reasoning";
}

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCallBlock(ToolCallId Id, string Name, string Arguments) : ContentBlock
{
    [JsonIgnore]
    public override string BlockType => "tool-call";
}

/// <summary>The result of a tool invocation, sent back to the model.</summary>
public sealed record ToolResultBlock(ToolCallId ToolCallId, IReadOnlyList<ContentBlock> Content, bool IsError = false) : ContentBlock
{
    [JsonIgnore]
    public override string BlockType => "tool-result";
}

