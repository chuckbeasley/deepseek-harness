using System.Text.Json;

namespace Dsh.Lsp;

/// <summary>
/// Pure protocol translation (port of <c>translate.ts</c>): what a server's capabilities allow, and how its
/// Location/LocationLink/Hover payloads normalize into the seam's closed result unions. No I/O or process
/// state — every member is a pure transform.
/// </summary>
public static class LspTranslate
{
    /// <summary>The four operations in their wire/display form (the TS union member strings).</summary>
    private static readonly string[] OperationNames = { "goToDefinition", "findReferences", "goToImplementation", "hover" };

    /// <summary>The <c>textDocument/*</c> request method for each LSP operation.</summary>
    /// <param name="operation">the LSP operation to map.</param>
    /// <returns>the LSP request method name.</returns>
    public static string RequestMethod(LspOperation operation)
    {
        return operation switch
        {
            LspOperation.GoToDefinition => "textDocument/definition",
            LspOperation.FindReferences => "textDocument/references",
            LspOperation.GoToImplementation => "textDocument/implementation",
            LspOperation.Hover => "textDocument/hover",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    /// <summary>The display name of one operation (for example <c>goToDefinition</c>), matching the TS union member strings.</summary>
    public static string OperationName(LspOperation operation)
    {
        return operation switch
        {
            LspOperation.GoToDefinition => OperationNames[0],
            LspOperation.FindReferences => OperationNames[1],
            LspOperation.GoToImplementation => OperationNames[2],
            LspOperation.Hover => OperationNames[3],
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    /// <summary>Parse a wire/display operation name; throws <see cref="ArgumentException"/> for anything else.</summary>
    /// <returns>the operation, or throws <c>operation must be one of ...</c>.</returns>
    public static LspOperation ParseOperation(string name)
    {
        switch (name)
        {
            case "goToDefinition": return LspOperation.GoToDefinition;
            case "findReferences": return LspOperation.FindReferences;
            case "goToImplementation": return LspOperation.GoToImplementation;
            case "hover": return LspOperation.Hover;
            default:
                throw new ArgumentException($"operation must be one of {string.Join(", ", OperationNames)}");
        }
    }

    /// <summary>Whether the server advertises the requested operation.</summary>
    /// <param name="capabilities">the server's <c>initialize</c> capabilities object.</param>
    /// <param name="operation">the LSP operation to check.</param>
    /// <returns>true when the corresponding provider slot is present (boolean or options form).</returns>
    public static bool SupportsOperation(JsonElement capabilities, LspOperation operation)
    {
        var slot = operation switch
        {
            LspOperation.GoToDefinition => "definitionProvider",
            LspOperation.FindReferences => "referencesProvider",
            LspOperation.GoToImplementation => "implementationProvider",
            LspOperation.Hover => "hoverProvider",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (!capabilities.TryGetProperty(slot, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            // Options objects (and any non-boolean value, like the TS typeof check) mean supported.
            _ => true,
        };
    }

    /// <summary>
    /// Whether a <c>textDocumentSync</c> value permits the transient didOpen/didClose this host relies on.
    /// The legacy enum form implies open/close for Full/Incremental; the options form requires an explicit
    /// <c>openClose: true</c>, because the protocol defaults an omitted <c>openClose</c> to false.
    /// </summary>
    /// <param name="sync">the server's advertised <c>textDocumentSync</c> capability, if any.</param>
    /// <returns>true when transient open/close is supported.</returns>
    public static bool SupportsTransientOpen(JsonElement? sync)
    {
        if (!sync.HasValue) return false;
        var value = sync.Value;
        if (value.ValueKind == JsonValueKind.Number) return value.GetInt32() == 1 || value.GetInt32() == 2;
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.TryGetProperty("openClose", out var openClose) && openClose.ValueKind == JsonValueKind.True;
        }
        return false;
    }

    /// <summary>
    /// Normalize the negotiated position encoding. An omitted encoding defaults to <c>utf-16</c>; any value
    /// other than <c>utf-16</c> is a protocol error this host does not support.
    /// </summary>
    /// <param name="encoding">the server's advertised <c>positionEncoding</c>, if any.</param>
    /// <returns>the string <c>utf-16</c>.</returns>
    public static string NegotiatePositionEncoding(string? encoding)
    {
        if (encoding is null || encoding == "utf-16") return "utf-16";
        throw new InvalidOperationException($"server negotiated unsupported position encoding \"{encoding}\"; this host requires utf-16");
    }

    /// <summary>
    /// Normalize a navigation result (Location, Location[], LocationLink[], or JSON null) to the seam's
    /// locations. Location maps directly; LocationLink maps <c>targetUri</c> + <c>targetSelectionRange</c>.
    /// </summary>
    /// <param name="payload">the raw <c>textDocument/definition|references|implementation</c> result (null when missing).</param>
    /// <returns>the normalized locations (empty for JSON null).</returns>
    public static IReadOnlyList<LspLocation> NormalizeLocations(JsonElement? payload)
    {
        if (!payload.HasValue) throw Malformed("LSP navigation result was missing");
        var value = payload.Value;
        if (value.ValueKind == JsonValueKind.Null) return Array.Empty<LspLocation>();
        var elements = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : new[] { value };
        var locations = new List<LspLocation>();
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Malformed("LSP navigation result contained a non-object entry");
            }
            if (IsLocationLink(element))
            {
                locations.Add(new LspLocation(element.GetProperty("targetUri").GetString()!, ToRange(element.GetProperty("targetSelectionRange"))));
            }
            else if (IsLocation(element))
            {
                locations.Add(new LspLocation(element.GetProperty("uri").GetString()!, ToRange(element.GetProperty("range"))));
            }
            else
            {
                throw Malformed("LSP navigation result contained neither a Location nor a LocationLink");
            }
        }
        return locations;
    }

    /// <summary>
    /// Normalize a Hover (or JSON null) to the seam's hover. MarkupContent uses its value; a string
    /// MarkedString is verbatim; a language-tagged MarkedString becomes a fenced code block; an array joins
    /// its rendered parts with one blank line.
    /// </summary>
    /// <param name="payload">the raw <c>textDocument/hover</c> result (null when missing).</param>
    /// <returns>the normalized hover, or null when there is no content.</returns>
    public static LspHover? NormalizeHover(JsonElement? payload)
    {
        if (!payload.HasValue) throw Malformed("LSP hover result was missing");
        var value = payload.Value;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Malformed("LSP hover result was not an object");
        var contents = RenderHoverContents(value);
        if (contents.Length == 0) return null;
        if (!value.TryGetProperty("range", out var range)) return new LspHover(contents);
        if (!IsRange(range)) throw Malformed("LSP hover result contained a malformed range");
        return new LspHover(contents, ToRange(range));
    }

    /// <summary>Render the three Hover.contents encodings into one string (input is untrusted wire data).</summary>
    private static string RenderHoverContents(JsonElement hover)
    {
        if (!hover.TryGetProperty("contents", out var contents) || contents.ValueKind == JsonValueKind.Null)
        {
            throw Malformed("LSP hover result had no contents");
        }
        switch (contents.ValueKind)
        {
            case JsonValueKind.String:
                return contents.GetString()!;
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var member in contents.EnumerateArray())
                {
                    if (!IsMarkedString(member)) throw Malformed("LSP hover contents contained a malformed MarkedString");
                    parts.Add(RenderMarkedString(member));
                }
                return string.Join("\n\n", parts);
            case JsonValueKind.Object:
                if (TryGetString(contents, "kind", out var kind) && (kind == "markdown" || kind == "plaintext"))
                {
                    if (!TryGetString(contents, "value", out var value))
                    {
                        throw Malformed("LSP hover MarkupContent value was not a string");
                    }
                    return value;
                }
                if (TryGetString(contents, "language", out _) && TryGetString(contents, "value", out var marked))
                {
                    return RenderMarkedString(contents);
                }
                throw Malformed("LSP hover contents were not MarkupContent, MarkedString, or an array");
            default:
                throw Malformed("LSP hover contents were not MarkupContent, MarkedString, or an array");
        }
    }

    /// <summary>Render one MarkedString: string form verbatim, object form as a language-tagged fenced block.</summary>
    private static string RenderMarkedString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        return $"```{value.GetProperty("language").GetString()}\n{value.GetProperty("value").GetString()}\n```";
    }

    /// <summary>Whether an untrusted value is either form of MarkedString.</summary>
    private static bool IsMarkedString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return true;
        if (value.ValueKind != JsonValueKind.Object) return false;
        return TryGetString(value, "language", out _) && TryGetString(value, "value", out _);
    }

    /// <summary>Whether a record is a LocationLink (has string <c>targetUri</c> + a valid <c>targetSelectionRange</c>).</summary>
    private static bool IsLocationLink(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty("targetUri", out var uri) && uri.ValueKind == JsonValueKind.String
           && value.TryGetProperty("targetSelectionRange", out var range) && IsRange(range);

    /// <summary>Whether a record is a Location (has string <c>uri</c> + a valid <c>range</c>).</summary>
    private static bool IsLocation(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String
           && value.TryGetProperty("range", out var range) && IsRange(range);

    /// <summary>Structural range guard used by both location shapes.</summary>
    private static bool IsRange(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty("start", out var start) && IsPosition(start)
           && value.TryGetProperty("end", out var end) && IsPosition(end);

    /// <summary>Structural position guard: object with <c>line</c> and <c>character</c> both non-negative integers.</summary>
    private static bool IsPosition(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty("line", out var line) && IsCoordinate(line)
           && value.TryGetProperty("character", out var character) && IsCoordinate(character);

    /// <summary>Whether a wire coordinate is a valid nonnegative integer.</summary>
    private static bool IsCoordinate(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0;

    /// <summary>Copy a validated wire range verbatim into the seam's range.</summary>
    private static LspRange ToRange(JsonElement range)
    {
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        return new LspRange(
            new LspPosition((int)start.GetProperty("line").GetInt64(), (int)start.GetProperty("character").GetInt64()),
            new LspPosition((int)end.GetProperty("line").GetInt64(), (int)end.GetProperty("character").GetInt64()));
    }

    /// <summary>Read a string property when present and string-typed.</summary>
    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Create the stable structured error used for malformed server result payloads.</summary>
    private static LspError Malformed(string message) => new(message, "LSP_MALFORMED_RESPONSE");
}
