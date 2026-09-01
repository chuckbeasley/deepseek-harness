using System.Text.Json.Serialization;

namespace Harness.Llm;

/// <summary>
/// Raw streaming protocol emitted by adapters. Block indexes correlate interleaved deltas, and
/// block-end carries the assembled block. Adapters emit usage before the terminal finish and
/// nothing afterward; tool arguments remain raw JSON strings.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BlockStart), "block-start")]
[JsonDerivedType(typeof(TextDelta), "text-delta")]
[JsonDerivedType(typeof(ReasoningDelta), "reasoning-delta")]
[JsonDerivedType(typeof(ToolCallDelta), "tool-call-delta")]
[JsonDerivedType(typeof(BlockEnd), "block-end")]
[JsonDerivedType(typeof(UsageChunk), "usage")]
[JsonDerivedType(typeof(Finish), "finish")]
public abstract record StreamChunk;

/// <summary>Opens a block at <see cref="Index"/> of the given block type.</summary>
public sealed record BlockStart(int Index, string BlockType) : StreamChunk;

/// <summary>Accumulates visible text for the block at <see cref="Index"/>.</summary>
public sealed record TextDelta(int Index, string Text) : StreamChunk;

/// <summary>Accumulates reasoning text for the block at <see cref="Index"/>.</summary>
public sealed record ReasoningDelta(int Index, string Text) : StreamChunk;

/// <summary>Accumulates one tool call's arguments for the block at <see cref="Index"/>.</summary>
public sealed record ToolCallDelta(int Index, ToolCallId Id, string? Name, string ArgumentsDelta) : StreamChunk;

/// <summary>Closes the block at <see cref="Index"/> with its assembled block.</summary>
public sealed record BlockEnd(int Index, ContentBlock Block) : StreamChunk;

/// <summary>Token accounting for the call.</summary>
public sealed record UsageChunk([property: JsonPropertyName("usage")] TokenUsage Usage) : StreamChunk;

/// <summary>Terminal outcome of the call, with optional replay metadata for a successful response.</summary>
public sealed record Finish(FinishReason Reason, ReplayEnvelope? ReplayState = null) : StreamChunk;


