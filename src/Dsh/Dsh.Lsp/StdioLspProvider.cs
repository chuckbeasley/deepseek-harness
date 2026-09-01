namespace Harness.Lsp;

/// <summary>
/// One stdio language-server provider (port of the TS lsp-stdio provider, single-server form): a
/// configured launch spec plus the extension-to-language route map. The provider owns one lazily
/// spawned <see cref="LspInstance"/> (initialize on first query, graceful teardown on dispose);
/// the recorded corpus exercises a single query, so per-provider pooling is deferred with the
/// Wave 2 pool.
/// </summary>
public sealed class StdioLspProvider : ILspProvider, IAsyncDisposable
{
    private readonly LspInstanceSpec _spec;
    private readonly IReadOnlyDictionary<string, string> _extensionToLanguage;
    private LspInstance? _instance;

    /// <summary>Create the provider; the server spawns lazily on the first query.</summary>
    /// <param name="id">the provider's branded id.</param>
    /// <param name="spec">the launch, initialize, and teardown parameters (cwd is also the document root).</param>
    /// <param name="extensionToLanguage">final-extension (lowercase, leading dot) to language id map.</param>
    public StdioLspProvider(LspProviderId id, LspInstanceSpec spec, IReadOnlyDictionary<string, string> extensionToLanguage)
    {
        if (extensionToLanguage.Count == 0)
        {
            throw new LspError("lsp-stdio: a provider must own at least one extension", "LSP_INVALID_PROVIDER");
        }
        Id = id;
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _extensionToLanguage = extensionToLanguage;
    }

    /// <inheritdoc />
    public LspProviderId Id { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ExtensionToLanguage => _extensionToLanguage;

    /// <inheritdoc />
    public async Task<LspQueryResult> QueryAsync(LspProviderQuery request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instance = _instance ??= new LspInstance(_spec, LspConnection.DefaultSpawner);
        var path = Path.IsPathRooted(request.FilePath)
            ? request.FilePath
            : Path.Combine(_spec.Cwd, request.FilePath);
        var text = File.ReadAllText(path);
        return await instance.QueryAsync(request, new HostSource(FileUrl(path), text), ct).ConfigureAwait(false);
    }

    /// <summary>Graceful teardown: shut down and terminate the spawned server, if any.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_instance is not null)
        {
            await _instance.DisposeAsync().ConfigureAwait(false);
            _instance = null;
        }
    }

    private static string FileUrl(string path) => new Uri(path).AbsoluteUri;
}