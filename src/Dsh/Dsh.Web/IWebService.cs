namespace Harness.Web;

/// <summary>
/// Service Definition of the web access capability seam (ctx.web): the search and fetch request
/// and result vocabulary, the two provider roles, and the execution surface. Search and fetch
/// deliberately share one seam so provider selection, errors, and configuration have one owner,
/// while keeping separate request and result types (port of <c>@deepseek-ai/dsh-web</c>).
/// </summary>
public static class WebSeam
{
    /// <summary>What one search-capable backend is asked to search. Each request carries one query.</summary>
    public sealed record SearchRequest(string Query, int? MaxResults = null, Harness.Session.Session? Session = null);

    /// <summary>One citeable source; a source always has a URL, the other fields are provider-optional.</summary>
    public sealed record SearchSource(string Url, string? Title = null, string? Snippet = null, string? PublishedAt = null);

    /// <summary>
    /// Normalized search outcome. <see cref="Content"/> is optional provider-generated answer text;
    /// <see cref="Sources"/> is the portable citation shape, already truncated to the request's
    /// <see cref="SearchRequest.MaxResults"/> by the seam; <see cref="Truncated"/> reports that cut.
    /// </summary>
    public sealed record SearchResult(string? Content, IReadOnlyList<SearchSource> Sources, bool Truncated);

    /// <summary>What one fetch-capable backend is asked to retrieve. Cancellation is an execution argument.</summary>
    public sealed record FetchRequest(string Url);

    /// <summary>The decoded body of a fetched resource: a closed union owned by the seam.</summary>
    public abstract record FetchBody
    {
        /// <summary>The body kind tag ("html" | "text" on the wire).</summary>
        public abstract string Kind { get; }
    }

    /// <summary>A body classified as HTML.</summary>
    public sealed record HtmlBody(string Content) : FetchBody
    {
        /// <inheritdoc />
        public override string Kind => "html";
    }

    /// <summary>A body classified as plain text.</summary>
    public sealed record TextBody(string Content) : FetchBody
    {
        /// <inheritdoc />
        public override string Kind => "text";
    }

    /// <summary>
    /// Normalized fetch outcome. A successful network fetch of a non-2xx response is a result, not
    /// an error: <see cref="StatusCode"/> is part of the fetched resource state. <see cref="WebError"/>
    /// is reserved for failures to safely retrieve or represent the resource.
    /// </summary>
    public sealed record FetchResult(string Url, int StatusCode, FetchBody Body, bool Truncated);

    /// <summary>A provider registered into the seam; <see cref="Id"/> is stable and unique per kind.</summary>
    public interface IProvider
    {
        /// <summary>Stable registration id, unique within the capability kind.</summary>
        string Id { get; }

        /// <summary>Cheap local usability check; must not make network calls.</summary>
        bool Available();
    }

    /// <summary>A search-capable backend.</summary>
    public interface ISearchProvider : IProvider
    {
        /// <summary>Run one search; honor <paramref name="cancellationToken"/> for cancellation.</summary>
        Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    }

    /// <summary>A fetch-capable backend.</summary>
    public interface IFetchProvider : IProvider
    {
        /// <summary>Retrieve one URL; honor <paramref name="cancellationToken"/> for cancellation.</summary>
        Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken);
    }
}

/// <summary>
/// Typed web error with a machine-routable, open-string <see cref="Code"/>. Shared codes cover
/// unavailable, missing, unusable, ambiguous, or duplicate providers, cancellation, and provider
/// failure; the local fetch provider additionally distinguishes invalid or blocked URLs, redirects,
/// size and timeout limits, and unsupported content types.
/// </summary>
public sealed class WebError : Exception, Harness.Tools.IToolErrorInfo
{
    /// <summary>Create the error; <paramref name="code"/> is the stable machine code.</summary>
    public WebError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Create the error with a chained <paramref name="inner"/> cause.</summary>
    public WebError(string message, string code, Exception? inner)
        : base(message, inner)
    {
        Code = code;
    }

    /// <inheritdoc />
    public string Name => "WebError";

    /// <summary>Stable machine-routable code; consumers must tolerate provider-specific codes.</summary>
    public string Code { get; }
}

/// <summary>
/// The web access service (ctx.web): provider registries plus provider-selecting execution for
/// search and fetch. Duplicate ids are rejected at registration. At execution time a configured
/// provider must exist and be usable; without a configured id, exactly one usable provider is
/// required, so selection never depends on registration order. Port of <c>@deepseek-ai/dsh-web</c>
/// WebRuntime.
/// </summary>
public interface IWebService
{
    /// <summary>
    /// Register a search provider under its <see cref="WebSeam.IProvider.Id"/>; the registration is
    /// an effect, so disposing the returned disposer (or the context) unregisters it.
    /// </summary>
    /// <exception cref="WebError">code <c>WEB_DUPLICATE_PROVIDER</c> when the id is already registered.</exception>
    IDisposable RegisterSearchProvider(WebSeam.ISearchProvider provider);

    /// <summary>
    /// Register a fetch provider under its <see cref="WebSeam.IProvider.Id"/>; the registration is
    /// an effect, so disposing the returned disposer (or the context) unregisters it.
    /// </summary>
    /// <exception cref="WebError">code <c>WEB_DUPLICATE_PROVIDER</c> when the id is already registered.</exception>
    IDisposable RegisterFetchProvider(WebSeam.IFetchProvider provider);

    /// <summary>
    /// Run one search through the selected provider. The seam enforces
    /// <see cref="WebSeam.SearchRequest.MaxResults"/> on the result: an over-returning provider gets
    /// its sources truncated and <see cref="WebSeam.SearchResult.Truncated"/> set.
    /// </summary>
    /// <exception cref="WebError">when the capability cannot run (provider missing/unavailable/ambiguous).</exception>
    Task<WebSeam.SearchResult> SearchAsync(WebSeam.SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retrieve one URL through the selected provider; a non-2xx response is a result, not a throw.</summary>
    /// <exception cref="WebError">when the capability cannot run (provider missing/unavailable/ambiguous).</exception>
    Task<WebSeam.FetchResult> FetchAsync(WebSeam.FetchRequest request, CancellationToken cancellationToken = default);
}
