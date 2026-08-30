using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>Why a model response stopped. Merge-extensible so adapters can surface provider-specific reasons.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Stop), "stop")]
[JsonDerivedType(typeof(ToolCalls), "tool-calls")]
[JsonDerivedType(typeof(MaxTokens), "max-tokens")]
[JsonDerivedType(typeof(Aborted), "aborted")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record FinishReason;

/// <summary>The model finished normally.</summary>
public sealed record Stop : FinishReason;

/// <summary>The model stopped because it requested tool calls.</summary>
public sealed record ToolCalls : FinishReason;

/// <summary>The model stopped because it hit its output-token ceiling.</summary>
public sealed record MaxTokens : FinishReason;

/// <summary>The call was aborted mid-stream.</summary>
public sealed record Aborted(LlmFailure Failure) : FinishReason;

/// <summary>The call failed.</summary>
public sealed record Error(LlmFailure Failure) : FinishReason;
