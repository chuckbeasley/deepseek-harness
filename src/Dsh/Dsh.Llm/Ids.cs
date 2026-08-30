using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>Common shape of the branded string ids: a nominal wrapper over one string value.</summary>
public interface IStringId
{
    /// <summary>The wrapped raw string.</summary>
    string Value { get; }
}

/// <summary>Serializes a branded id as its bare string value (the TS wire shape).</summary>
public sealed class StringIdJsonConverter<T> : JsonConverter<T> where T : struct, IStringId
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (T)Activator.CreateInstance(typeof(T), reader.GetString() ?? string.Empty)!;

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Stable identity carried by one message across inbox, log, and model-request boundaries.</summary>
[JsonConverter(typeof(StringIdJsonConverter<MessageId>))]
public readonly record struct MessageId(string Value) : IStringId
{
    public static implicit operator string(MessageId id) => id.Value;
    public override string ToString() => Value;
}

/// <summary>Correlates a model-issued tool call with its result. Provider-issued or synthesized.</summary>
[JsonConverter(typeof(StringIdJsonConverter<ToolCallId>))]
public readonly record struct ToolCallId(string Value) : IStringId
{
    public static implicit operator string(ToolCallId id) => id.Value;
    public override string ToString() => Value;
}

/// <summary>Provider-issued request identifier retained for diagnostics across package boundaries.</summary>
[JsonConverter(typeof(StringIdJsonConverter<ProviderRequestId>))]
public readonly record struct ProviderRequestId(string Value) : IStringId
{
    public static implicit operator string(ProviderRequestId id) => id.Value;
    public override string ToString() => Value;
}

/// <summary>Adapter-owned identifier for one model's selectable reasoning effort.</summary>
[JsonConverter(typeof(StringIdJsonConverter<ReasoningEffortId>))]
public readonly record struct ReasoningEffortId(string Value) : IStringId
{
    public static implicit operator string(ReasoningEffortId id) => id.Value;
    public override string ToString() => Value;
}
