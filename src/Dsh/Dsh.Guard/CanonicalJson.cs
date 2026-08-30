using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dsh.Guard;

/// <summary>
/// Canonical JSON text: deep key-sort of parsed JSON values so two argument documents that differ
/// only in property order canonicalize identically (port of the TS repeat-reminder
/// <c>sortJsonValue</c>/<c>canonicalize</c>). Arguments reach the guard as the loop's parsed
/// <c>tool/call</c> JSON (or its raw-string fallback for malformed argument JSON), so JSON's value
/// domain is the whole input domain — no cycle or undefined handling exists because no input path
/// can produce them.
/// </summary>
public static class CanonicalJson
{
    /// <summary>
    /// Canonical string form of a call's arguments: deep key-sort, then compact stringify. A
    /// document that is not valid JSON canonicalizes to its raw text, matching the loop's
    /// raw-string fallback for malformed argument JSON.
    /// </summary>
    public static string Canonicalize(string rawArguments)
    {
        ArgumentNullException.ThrowIfNull(rawArguments);
        try
        {
            using var document = JsonDocument.Parse(rawArguments);
            var builder = new StringBuilder(rawArguments.Length);
            WriteCanonical(builder, document.RootElement);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return rawArguments;
        }
    }

    private static void WriteCanonical(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                builder.Append('{');
                for (var index = 0; index < properties.Length; index++)
                {
                    if (index > 0) builder.Append(',');
                    builder.Append('"').Append(JsonEncodedText.Encode(properties[index].Name)).Append("\":");
                    WriteCanonical(builder, properties[index].Value);
                }
                builder.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteCanonical(builder, item);
                }
                builder.Append(']');
                break;
            }
            case JsonValueKind.String:
                builder.Append('"').Append(JsonEncodedText.Encode(element.GetString() ?? string.Empty)).Append('"');
                break;
            case JsonValueKind.Number:
                // Normalize like JSON.stringify: an integral value renders without a fraction.
                builder.Append(element.TryGetInt64(out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : element.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }
}
