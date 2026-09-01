namespace Harness.Lsp;

/// <summary>Typed LSP failure carrying a stable machine-readable code (port of the TS <c>LspError</c>).</summary>
public sealed class LspError : Exception
{
    /// <summary>Create the error; <paramref name="code"/> is the stable machine-routing code.</summary>
    public LspError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine-routing code (for example <c>LSP_MALFORMED_RESPONSE</c>, <c>LSP_DISPOSED</c>).</summary>
    public string Code { get; }
}
