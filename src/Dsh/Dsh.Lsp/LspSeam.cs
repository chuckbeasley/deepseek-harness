namespace Harness.Lsp;

/// <summary>The four navigation operations the seam exposes (closed union in TS; the wire mapping lives in <see cref="LspTranslate"/>).</summary>
public enum LspOperation
{
    /// <summary>Jump to the definition of the symbol at the position.</summary>
    GoToDefinition,
    /// <summary>Find all references (always including the declaration) of the symbol at the position.</summary>
    FindReferences,
    /// <summary>Jump to the implementation of the symbol at the position.</summary>
    GoToImplementation,
    /// <summary>Show the hover documentation at the position.</summary>
    Hover,
}

/// <summary>Zero-based UTF-16 document coordinate (one-based only at the model-facing tool boundary).</summary>
public readonly record struct LspPosition(int Line, int Character);

/// <summary>Zero-based UTF-16 span inside one document.</summary>
public readonly record struct LspRange(LspPosition Start, LspPosition End);

/// <summary>One navigation hit: a target URI plus the selected range.</summary>
public sealed record LspLocation(string Uri, LspRange Range);

/// <summary>Normalized hover documentation with an optional highlight range.</summary>
public sealed record LspHover(string Contents, LspRange? Range = null);

/// <summary>A navigation query; every field is required and carries no defaults.</summary>
public record LspQueryRequest(LspOperation Operation, string FilePath, LspPosition Position, string WorkspaceRoot);

/// <summary>A provider-routed query; <paramref name="LanguageId"/> only synchronizes the transient document and never participates in selection.</summary>
public sealed record LspProviderQuery(LspOperation Operation, string FilePath, LspPosition Position, string WorkspaceRoot, string LanguageId)
    : LspQueryRequest(Operation, FilePath, Position, WorkspaceRoot);

/// <summary>Closed result union for one query; <see cref="Kind"/> is the discriminant.</summary>
public abstract record LspQueryResult(string Kind)
{
    /// <summary>Discriminant: <c>locations</c> or <c>hover</c>.</summary>
    public string Kind { get; } = Kind;
}

/// <summary>A navigation result; callers relativize location URIs against <see cref="ResolvedWorkspaceUri"/>, never against host path rules.</summary>
public sealed record LspLocationsResult(IReadOnlyList<LspLocation> Locations, string ResolvedWorkspaceUri) : LspQueryResult("locations");

/// <summary>A hover result; <see cref="Hover"/> is null when the server had no documentation.</summary>
public sealed record LspHoverResult(LspHover? Hover) : LspQueryResult("hover");

/// <summary>Branded provider id; the factory performs no validation (the registry rejects empties).</summary>
public readonly record struct LspProviderId(string Value);

/// <summary>A language-server provider: a branded id, its extension→language map, and one query method.</summary>
public interface ILspProvider
{
    /// <summary>The provider's branded id.</summary>
    LspProviderId Id { get; }

    /// <summary>Final-extension (lowercase, leading dot) to language id mapping used for route selection.</summary>
    IReadOnlyDictionary<string, string> ExtensionToLanguage { get; }

    /// <summary>Run one query; the result is the closed union over locations and hover.</summary>
    Task<LspQueryResult> QueryAsync(LspProviderQuery request, CancellationToken ct = default);
}

/// <summary>
/// The LSP capability seam: provider registration plus per-query selection by the file's final extension.
/// The registry implementation (<c>LspService</c>) arrives with the Wave 2 pooling work.
/// </summary>
public interface ILspService
{
    /// <summary>Register a provider (atomic id + extension reservation); the returned action unregisters it.</summary>
    Action RegisterProvider(ILspProvider provider);

    /// <summary>Route one query to the provider handling the file's final extension.</summary>
    Task<LspQueryResult> QueryAsync(LspQueryRequest request, CancellationToken ct = default);
}

/// <summary>
/// The pre-validated document an instance opens transiently. Wave 2's <c>LspHost.ReadHostSource</c> produces
/// this; the instance consumes it directly and never reads the file itself.
/// </summary>
public sealed record HostSource(string FileUrl, string Text);
