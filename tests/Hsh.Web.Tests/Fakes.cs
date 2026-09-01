using Harness.Web;

namespace Harness.Web.Tests;

/// <summary>A search provider whose behavior is scripted per test.</summary>
internal sealed class FakeSearchProvider : WebSeam.ISearchProvider
{
    private readonly bool _available;
    private readonly Func<WebSeam.SearchRequest, CancellationToken, Task<WebSeam.SearchResult>> _search;

    public FakeSearchProvider(string id, bool available = true,
        Func<WebSeam.SearchRequest, CancellationToken, Task<WebSeam.SearchResult>>? search = null)
    {
        Id = id;
        _available = available;
        _search = search ?? DefaultSearch;
    }

    public string Id { get; }

    public bool Available() => _available;

    public Task<WebSeam.SearchResult> SearchAsync(WebSeam.SearchRequest request, CancellationToken cancellationToken)
        => _search(request, cancellationToken);

    private static Task<WebSeam.SearchResult> DefaultSearch(WebSeam.SearchRequest request, CancellationToken _)
        => Task.FromResult(new WebSeam.SearchResult(null, Array.Empty<WebSeam.SearchSource>(), false));
}

/// <summary>A fetch provider whose behavior is scripted per test.</summary>
internal sealed class FakeFetchProvider : WebSeam.IFetchProvider
{
    private readonly bool _available;
    private readonly Func<WebSeam.FetchRequest, CancellationToken, Task<WebSeam.FetchResult>> _fetch;

    public FakeFetchProvider(string id, bool available = true,
        Func<WebSeam.FetchRequest, CancellationToken, Task<WebSeam.FetchResult>>? fetch = null)
    {
        Id = id;
        _available = available;
        _fetch = fetch ?? DefaultFetch;
    }

    public string Id { get; }

    public bool Available() => _available;

    public Task<WebSeam.FetchResult> FetchAsync(WebSeam.FetchRequest request, CancellationToken cancellationToken)
        => _fetch(request, cancellationToken);

    private static Task<WebSeam.FetchResult> DefaultFetch(WebSeam.FetchRequest request, CancellationToken _)
        => Task.FromResult(new WebSeam.FetchResult(request.Url, 200, new WebSeam.TextBody("ok"), false));
}

/// <summary>Standard search sources for merge tests.</summary>
internal static class Sources
{
    public static WebSeam.SearchSource At(string url, string? title = null, string? snippet = null)
        => new(url, title, snippet, null);
}



