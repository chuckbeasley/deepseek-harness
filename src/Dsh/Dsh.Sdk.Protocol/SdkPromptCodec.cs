using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Sdk.Protocol;

/// <summary>
/// The wire codec for <see cref="SdkPromptContentBlock"/>: a block with <c>type: "image"</c>
/// decodes to <see cref="SdkPromptContentBlock.Image"/>, anything else decodes through the
/// session log's polymorphic <c>ContentBlock</c> codecs (the same wire the durable log speaks).
/// The inverse writes the image block explicitly or the content block polymorphically.
/// </summary>
public sealed class SdkPromptContentBlockConverter : JsonConverter<SdkPromptContentBlock>
{
    private static readonly JsonSerializerOptions ContentOptions =
        Dsh.Session.SessionEventTypes.CreateSerializerOptions();

    /// <inheritdoc />
    public override SdkPromptContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement.Clone();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "image")
        {
            return new SdkPromptContentBlock.Image(new SdkEncodedImageBlock(
                root.GetProperty("data").GetString()!,
                root.GetProperty("mimeType").GetString()!));
        }
        var block = root.Deserialize<Dsh.Llm.ContentBlock>(ContentOptions)
            ?? throw new JsonException("unparsable SDK prompt content block");
        return new SdkPromptContentBlock.Block(block);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SdkPromptContentBlock value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case SdkPromptContentBlock.Block(var block):
                JsonSerializer.Serialize(writer, block, ContentOptions);
                break;
            case SdkPromptContentBlock.Image(var image):
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteString("data", image.Data);
                writer.WriteString("mimeType", image.MimeType);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException("unknown SDK prompt content block kind");
        }
    }
}
