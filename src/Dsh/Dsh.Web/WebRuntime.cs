using Cordis.Core;
using Dsh.Llm;

namespace Dsh.Web;

/// <summary>Selection inputs for execution-time provider resolution.</summary>
public sealed record WebRuntimeConfig(
    /// <summary>Explicit search provider id; omitted = auto-select when exactly one usable provider is registered.</summary>
    string? SearchProvider = null,
    /// <summary>Explicit fetch provider id; omitted = auto-select when exactly one usable provider is registered.</summary>
    string? FetchProvider = null);

/// <summary>
/// The web access service registered as <c>ctx.web</c> (one instance per context). Implements
/// <see cref="IWebService"/> with the execution-time selection rules:
/// <list type="bullet">
/// <item>a configured id that is registered and available wins;</item>
/// <item>a configured id not registered → <c>WEB_PROVIDER_CONFIGURED_MISSING</c>;</item>
/// <item>a configured id registered but unavailable → <c>WEB_PROVIDER_CONFIGURED_UNAVAILABLE</c>;</item>
/// <item>no id configured, exactly one usable provider → that provider;</item>
/// <item>no id configured, multiple usable providers → <c>WEB_PROVIDER_AMBIGUOUS</c>;</item>
/// <item>no id configured, no usable provider → <c>WEB_PROVIDER_UNAVAILABLE</c>.</item>
/// </list>
/// Operational environment overrides feed the SAME config fields (<c>DSH_WEB_SEARCH_PROVIDER</c> /
/// <c>DSH_WEB_FETCH_PROVIDER</c>) and are not a hidden priority chain.
/// </summary>
public sealed class WebRuntime : Service, IWebService
{
    private readonly Dictionary<string, WebSeam.ISearchProvider> _searchProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebSeam.IFetchProvider> _fetchProviders = new(StringComparer.Ordinal);
    private readonly string? _searchProviderId;
    private readonly string? _fetchProviderId;

    /// <summary>Create and register the web service under the <c>web</c> key.</summary>
    public WebRuntime(Context ctx, WebRuntimeConfig? config = null)
        : base(ctx, "web")
    {
        var cfg = config ?? new WebRuntimeConfig();
        _searchProviderId = cfg.SearchProvider ?? Environment.GetEnvironmentVariable("DSH_WEB_SEARCH_PROVIDER");
        _fetchProviderId = cfg.FetchProvider ?? Environment.GetEnvironmentVariable("DSH_WEB_FETCH_PROVIDER");
    }

    /// <inheritdoc />
    public IDisposable RegisterSearchProvider(WebSeam.ISearchProvider provider)
        => RegisterProvider(_searchProviders, provider, "search");

    /// <inheritdoc />
    public IDisposable RegisterFetchProvider(WebSeam.IFetchProvider provider)
        => RegisterProvider(_fetchProviders, provider, "fetch");

    private IDisposable RegisterProvider<T>(Dictionary<string, T> store, T provider, string kind)
        where T : WebSeam.IProvider
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Id.Length == 0)
        {
            throw new WebError($"a web {kind} provider id must be non-empty", "WEB_INVALID_PROVIDER");
        }
        return Ctx.Effect(() =>
        {
            if (store.ContainsKey(provider.Id))
            {
                throw new WebError($"a web provider with id \"{provider.Id}\" is already registered", "WEB_DUPLICATE_PROVIDER");
            }
            store[provider.Id] = provider;
            return new ActionDisposer(() => store.Remove(provider.Id));
        }, "web.registerProvider()");
    }

    /// <inheritdoc />
    public async Task<WebSeam.SearchResult> SearchAsync(WebSeam.SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = ResolveProvider(_searchProviders, _searchProviderId);
        var result = await provider.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        return CapSources(result, request.MaxResults);
    }

    /// <inheritdoc />
    public async Task<WebSeam.FetchResult> FetchAsync(WebSeam.FetchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = ResolveProvider(_fetchProviders, _fetchProviderId);
        return await provider.FetchAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static T ResolveProvider<T>(Dictionary<string, T> providers, string? configuredId)
        where T : WebSeam.IProvider
    {
        if (configuredId is not null)
        {
            if (!providers.TryGetValue(configuredId, out var provider))
            {
                throw new WebError($"configured web provider \"{configuredId}\" is not registered", "WEB_PROVIDER_CONFIGURED_MISSING");
            }
            if (!provider.Available())
            {
                throw new WebError($"configured web provider \"{configuredId}\" is registered but unavailable", "WEB_PROVIDER_CONFIGURED_UNAVAILABLE");
            }
            return provider;
        }
        var usable = providers.Values.Where(p => p.Available()).ToArray();
        if (usable.Length == 0)
        {
            throw new WebError("no usable web provider is registered", "WEB_PROVIDER_UNAVAILABLE");
        }
        if (usable.Length > 1)
        {
            var ids = string.Join(", ", usable.Select(p => p.Id));
            throw new WebError($"multiple usable web providers are registered ({ids}); configure one explicitly", "WEB_PROVIDER_AMBIGUOUS");
        }
        return usable[0];
    }

    /// <summary>Enforce <c>maxResults</c> on a search result: truncate sources and flag it.</summary>
    private static WebSeam.SearchResult CapSources(WebSeam.SearchResult result, int? maxResults)
    {
        if (maxResults is null || result.Sources.Count <= maxResults.Value)
        {
            return result;
        }
        return result with
        {
            Sources = result.Sources.Take(maxResults.Value).ToArray(),
            Truncated = true,
        };
    }
}

