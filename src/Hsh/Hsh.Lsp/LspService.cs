using Harness.Cordis.Core;

namespace Harness.Lsp;

/// <summary>
/// The LSP capability service (ctx.lsp): provider registration plus per-query selection by the
/// file's final extension (port of the TS lsp service). The registry is the Wave 2 pooling
/// boundary: providers register their id and extension reservation atomically, and queries route
/// to the provider whose map owns the file's final extension.
/// </summary>
public sealed class LspService : Service, ILspService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ILspProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>Create and register the service as <c>lsp</c>.</summary>
    public LspService(Context ctx)
        : base(ctx, "lsp")
    {
    }

    /// <inheritdoc />
    public Action RegisterProvider(ILspProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Id.Value.Trim().Length == 0)
        {
            throw new LspError("lsp: a provider id must be non-empty", "LSP_INVALID_PROVIDER");
        }
        if (provider.ExtensionToLanguage.Count == 0)
        {
            throw new LspError("lsp: a provider must own at least one extension", "LSP_INVALID_PROVIDER");
        }
        lock (_gate)
        {
            if (_providers.ContainsKey(provider.Id.Value))
            {
                throw new LspError($"a provider named \"{provider.Id.Value}\" is already registered", "LSP_DUPLICATE_PROVIDER");
            }
            _providers.Add(provider.Id.Value, provider);
        }
        return () =>
        {
            lock (_gate) _providers.Remove(provider.Id.Value);
        };
    }

    /// <inheritdoc />
    public async Task<LspQueryResult> QueryAsync(LspQueryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var extension = FinalExtension(request.FilePath);
        ILspProvider? provider;
        lock (_gate)
        {
            provider = _providers.Values.FirstOrDefault(candidate => candidate.ExtensionToLanguage.ContainsKey(extension));
            if (provider is null)
            {
                throw new LspError($"no LSP provider handles {extension} files", "LSP_NO_PROVIDER");
            }
        }
        var languageId = provider.ExtensionToLanguage[extension];
        return await provider.QueryAsync(new LspProviderQuery(
            request.Operation, request.FilePath, request.Position, request.WorkspaceRoot, languageId), ct).ConfigureAwait(false);
    }

    /// <summary>The file's final extension, lowercase with the leading dot (the TS route key).</summary>
    private static string FinalExtension(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[dot..].ToLowerInvariant();
    }
}