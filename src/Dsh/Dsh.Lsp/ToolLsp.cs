namespace Dsh.Lsp;

/// <summary>The raw, schema-typed <c>lsp</c> tool arguments (one-based model coordinates).</summary>
public sealed record LspToolArgs(string Operation, string FilePath, int Line, int Character);

/// <summary>Validated <c>lsp</c> arguments after coordinate checks (zero-based seam position).</summary>
public sealed record LspToolInput(LspOperation Operation, string FilePath, LspPosition Position);

/// <summary>UI presentation for a pending <c>lsp</c> call (the shared generic search card shape).</summary>
public sealed record LspCallView(string Card, string Kind, string Title, IReadOnlyList<LspCallLocation> Locations);

/// <summary>One focused location line in a call view.</summary>
public sealed record LspCallLocation(string Path, int Line);

/// <summary>
/// The model-facing <c>lsp</c> tool (port of <c>tool-lsp</c>, Wave 3 subset): argument parsing and pure
/// rendering only — tool registration against the harness tools runtime arrives with the plugin wiring.
/// </summary>
public static class ToolLsp
{
    /// <summary>The four operations the tool exposes, in schema-enum order.</summary>
    public static readonly string[] Operations = { "goToDefinition", "findReferences", "goToImplementation", "hover" };

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
}
