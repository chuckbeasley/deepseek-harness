using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Llm;
using Harness.Tools;

namespace Harness.Lsp;

/// <summary>The raw, schema-typed <c>lsp</c> tool arguments (one-based model coordinates).</summary>
public sealed record LspToolArgs(string Operation, string FilePath, int Line, int Character);

/// <summary>Validated <c>lsp</c> arguments after coordinate checks (zero-based seam position).</summary>
public sealed record LspToolInput(LspOperation Operation, string FilePath, LspPosition Position);

/// <summary>UI presentation for a pending <c>lsp</c> call (the shared generic search card shape).</summary>
public sealed record LspCallView(string Card, string Kind, string Title, IReadOnlyList<LspCallLocation> Locations);

/// <summary>One focused location line in a call view.</summary>
public sealed record LspCallLocation(string Path, int Line);

/// <summary>
/// The model-facing <c>lsp</c> tool (port of tool-lsp): argument parsing, the seam query, and the
/// pure location/hover rendering. The tool result carries no persisted meta (the recorded corpus
/// shape).
/// </summary>
public static class ToolLsp
{
    private const string ParametersSchema =
        "{\"operation\":{\"type\":\"string\",\"required\":true,\"enum\":[\"goToDefinition\",\"findReferences\",\"goToImplementation\",\"hover\"]},"
        + "\"file_path\":{\"type\":\"string\",\"required\":true},"
        + "\"line\":{\"type\":\"number\",\"required\":true},"
        + "\"character\":{\"type\":\"number\",\"required\":true}}";

    /// <summary>
    /// Validate and convert model arguments: <c>operation</c> must be one of the four; <c>line</c> and
    /// <c>character</c> are positive one-based integers converted to the seam's zero-based position.
    /// </summary>
    /// <param name="args">the schema-validated raw arguments.</param>
    /// <returns>the validated input with a zero-based position.</returns>
    public static LspToolInput ParseLspArgs(LspToolArgs args)
    {
        var operation = LspTranslate.ParseOperation(args.Operation);
        if (string.IsNullOrWhiteSpace(args.FilePath)) throw new ArgumentException("file_path must be a non-empty string");
        if (args.Line < 1) throw new ArgumentException("line must be a positive integer (one-based)");
        if (args.Character < 1) throw new ArgumentException("character must be a positive integer (one-based)");
        // The model counts from 1; the seam (and protocol) count from 0.
        return new LspToolInput(operation, args.FilePath, new LspPosition(args.Line - 1, args.Character - 1));
    }

    /// <summary>Build the tool over the mounted LSP service.</summary>
    /// <param name="service">the routing service (ctx.lsp).</param>
    /// <param name="maxLocations">the cap before the omission marker is appended.</param>
    /// <param name="maxResultChars">the complete rendered-text cap.</param>
    public static ToolDefinition Definition(ILspService service, int maxLocations = LspRender.DefaultMaxLocations, int maxResultChars = LspRender.DefaultMaxResultChars)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new ToolDefinition(
            Name: "lsp",
            Description: "Query a language server for navigation and documentation at a file position (one-based line and character).",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchema)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"kind\":{\"type\":\"string\",\"required\":true},\"text\":{\"type\":\"string\",\"required\":true}}}")!),
            Execute: (args, context) => ExecuteAsync(service, args, context, maxLocations, maxResultChars),
            Render: (_, value) => new ContentBlock[] { new TextBlock(value.GetProperty("text").GetString() ?? string.Empty) },
            PersistMeta: false);
    }

    private static async Task<JsonElement> ExecuteAsync(
        ILspService service, JsonElement args, ToolRunContext context, int maxLocations, int maxResultChars)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("lsp: invalid arguments");
        }
        var parsed = new LspToolArgs(
            args.TryGetProperty("operation", out var operation) ? operation.GetString() ?? string.Empty : string.Empty,
            args.TryGetProperty("file_path", out var filePath) ? filePath.GetString() ?? string.Empty : string.Empty,
            args.TryGetProperty("line", out var line) ? line.GetInt32() : 0,
            args.TryGetProperty("character", out var character) ? character.GetInt32() : 0);
        var input = ToolLsp.ParseLspArgs(parsed);
        var result = await service.QueryAsync(new LspQueryRequest(
            input.Operation, input.FilePath, input.Position, WorkspaceRoot: Environment.CurrentDirectory), context.CancellationToken)
            .ConfigureAwait(false);
        var text = result switch
        {
            LspLocationsResult locations => LspRender.FormatLocations(locations.Locations, locations.ResolvedWorkspaceUri, maxLocations, maxResultChars),
            LspHoverResult hover => LspRender.FormatHover(hover.Hover, maxResultChars),
            _ => throw new InvalidOperationException($"lsp: unknown result kind {result.Kind}"),
        };
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["kind"] = result.Kind,
            ["text"] = text,
        });
    }
}