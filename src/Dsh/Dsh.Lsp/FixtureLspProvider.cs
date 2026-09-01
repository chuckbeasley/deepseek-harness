namespace Dsh.Lsp;

/// <summary>
/// Embedded fixture language server (the recorded corpus path; node is not used in the ported
/// version). Replicates the recorded lsp-server.mjs behavior exactly: the initialize capabilities
/// (utf-16 positions, sync 1, definition provider), a definition query returning the two recorded
/// locations (lines 0 and 1, character range 6-12) under the workspace root, and no other
/// operation support. The server owns no process; the query runs entirely in-process.
/// </summary>
public sealed class FixtureLspProvider : ILspProvider
{
    private readonly LspProviderId _id;
    private readonly IReadOnlyDictionary<string, string> _extensionToLanguage;
    private readonly string _documentName;

    /// <summary>Create the provider; <paramref name="documentName"/> is the recorded fixture document
    /// the server resolves relative to the workspace root (e.g. <c>subject.ts</c>).</summary>
    public FixtureLspProvider(LspProviderId id, IReadOnlyDictionary<string, string> extensionToLanguage, string documentName = "subject.ts")
    {
        _id = id;
        _extensionToLanguage = extensionToLanguage;
        _documentName = documentName;
    }

    /// <inheritdoc />
    public LspProviderId Id => _id;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ExtensionToLanguage => _extensionToLanguage;

    /// <inheritdoc />
    public Task<LspQueryResult> QueryAsync(LspProviderQuery request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (request.Operation != LspOperation.GoToDefinition)
        {
            throw new LspError($"server does not support {LspTranslate.OperationName(request.Operation)}", "LSP_UNSUPPORTED_OPERATION");
        }
        // The recorded server answers every definition query with the same two locations, at the
        // character range 6-12 of lines 0 and 1.
        var uri = new Uri(Path.Combine(request.WorkspaceRoot, _documentName)).AbsoluteUri;
        var locations = new[]
        {
            new LspLocation(uri, new LspRange(new LspPosition(0, 6), new LspPosition(0, 12))),
            new LspLocation(uri, new LspRange(new LspPosition(1, 6), new LspPosition(1, 12))),
        };
        return Task.FromResult<LspQueryResult>(new LspLocationsResult(locations, new Uri(request.WorkspaceRoot + Path.DirectorySeparatorChar).AbsoluteUri));
    }
}