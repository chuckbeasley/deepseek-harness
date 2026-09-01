using Harness.Cordis.Core;
using Harness.Web;

namespace Harness.Web.Tests;

/// <summary>Selection semantics and provider registry behavior of <see cref="WebRuntime"/>.</summary>
public static class WebRuntimeTests
{
    public static void Registered_UnderWebKey()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        Assert.Same(web, ctx.Get<WebRuntime>("web"));
    }

    public static void DuplicateSearchProvider_ThrowsDuplicate()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var first = web.RegisterSearchProvider(new FakeSearchProvider("a"));
        var error = Assert.Throws<WebError>(() => web.RegisterSearchProvider(new FakeSearchProvider("a")));
        Assert.Equal("WEB_DUPLICATE_PROVIDER", error.Code);
    }

    public static void DuplicateFetchProvider_ThrowsDuplicate()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var first = web.RegisterFetchProvider(new FakeFetchProvider("http"));
        var error = Assert.Throws<WebError>(() => web.RegisterFetchProvider(new FakeFetchProvider("http")));
        Assert.Equal("WEB_DUPLICATE_PROVIDER", error.Code);
    }

    public static void ConfiguredMissing_Throws()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx, new WebRuntimeConfig(SearchProvider: "ghost"));
        var error = Assert.ThrowsAny<WebError>(() => web.SearchAsync(new WebSeam.SearchRequest("q")));
        Assert.Equal("WEB_PROVIDER_CONFIGURED_MISSING", error.Code);
    }

    public static void ConfiguredUnavailable_Throws()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx, new WebRuntimeConfig(SearchProvider: "off"));
        using var registration = web.RegisterSearchProvider(new FakeSearchProvider("off", available: false));
        var error = Assert.ThrowsAny<WebError>(() => web.SearchAsync(new WebSeam.SearchRequest("q")));
        Assert.Equal("WEB_PROVIDER_CONFIGURED_UNAVAILABLE", error.Code);
    }

    public static void NoProvider_ThrowsUnavailable()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        var error = Assert.ThrowsAny<WebError>(() => web.SearchAsync(new WebSeam.SearchRequest("q")));
        Assert.Equal("WEB_PROVIDER_UNAVAILABLE", error.Code);
        Assert.True(error.Message.Contains("no usable web provider", StringComparison.Ordinal));
    }

    public static void MultipleUsable_ThrowsAmbiguous()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var a = web.RegisterSearchProvider(new FakeSearchProvider("a"));
        using var b = web.RegisterSearchProvider(new FakeSearchProvider("b"));
        var error = Assert.ThrowsAny<WebError>(() => web.SearchAsync(new WebSeam.SearchRequest("q")));
        Assert.Equal("WEB_PROVIDER_AMBIGUOUS", error.Code);
        Assert.True(error.Message.Contains("a, b", StringComparison.Ordinal));
    }

    public static void SingleUsable_AutoSelects()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = web.RegisterSearchProvider(new FakeSearchProvider("only"));
        var result = web.SearchAsync(new WebSeam.SearchRequest("q")).GetAwaiter().GetResult();
        Assert.False(result.Truncated);
        Assert.Empty(result.Sources);
    }

    public static void ConfiguredId_WinsOverOtherUsable()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx, new WebRuntimeConfig(SearchProvider: "pinned"));
        using var a = web.RegisterSearchProvider(new FakeSearchProvider("other"));
        var pinned = new FakeSearchProvider("pinned", search: (request, _) =>
            Task.FromResult(new WebSeam.SearchResult(null, new[] { Sources.At("https://pinned.example") }, false)));
        using var b = web.RegisterSearchProvider(pinned);
        var result = web.SearchAsync(new WebSeam.SearchRequest("q")).GetAwaiter().GetResult();
        Assert.Equal("https://pinned.example", Assert.Single(result.Sources).Url);
    }

    public static void SearchResult_IsCapped_ToMaxResults()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        var provider = new FakeSearchProvider("cap", search: (_, _) => Task.FromResult(new WebSeam.SearchResult(
            null,
            new[]
            {
                Sources.At("https://a.example"),
                Sources.At("https://b.example"),
                Sources.At("https://c.example"),
            },
            false)));
        using var registration = web.RegisterSearchProvider(provider);
        var result = web.SearchAsync(new WebSeam.SearchRequest("q", MaxResults: 2)).GetAwaiter().GetResult();
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal("https://a.example", result.Sources[0].Url);
        Assert.Equal("https://b.example", result.Sources[1].Url);
    }

    public static void SearchResult_Uncapped_WhenMaxResultsOmittedOrLarger()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        var provider = new FakeSearchProvider("cap", search: (_, _) => Task.FromResult(new WebSeam.SearchResult(
            null,
            new[] { Sources.At("https://a.example"), Sources.At("https://b.example") },
            false)));
        using var registration = web.RegisterSearchProvider(provider);
        var uncapped = web.SearchAsync(new WebSeam.SearchRequest("q")).GetAwaiter().GetResult();
        Assert.False(uncapped.Truncated);
        Assert.Equal(2, uncapped.Sources.Count);
        var larger = web.SearchAsync(new WebSeam.SearchRequest("q", MaxResults: 5)).GetAwaiter().GetResult();
        Assert.False(larger.Truncated);
        Assert.Equal(2, larger.Sources.Count);
    }

    public static void DisposingRegistration_UnregistersProvider()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        var registration = web.RegisterSearchProvider(new FakeSearchProvider("ephemeral"));
        registration.Dispose();
        var error = Assert.ThrowsAny<WebError>(() => web.SearchAsync(new WebSeam.SearchRequest("q")));
        Assert.Equal("WEB_PROVIDER_UNAVAILABLE", error.Code);
    }

    public static void Fetch_ResolvesThroughProvider()
    {
        using var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = web.RegisterFetchProvider(new FakeFetchProvider("http"));
        var result = web.FetchAsync(new WebSeam.FetchRequest("https://example.com")).GetAwaiter().GetResult();
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("ok", ((WebSeam.TextBody)result.Body).Content);
    }
}
