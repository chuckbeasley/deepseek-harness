using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Harness.Web;

/// <summary>
/// Resolved transport and response limits for <see cref="HttpWebProvider"/> (the plugin's default
/// values fill every field; validation is load-time, mirroring <c>@deepseek-ai/dsh-web-fetch-http</c>
/// Config). A size or length limit is a positive finite value; <see cref="MaxRedirects"/> is a
/// non-negative integer (0 follows no redirects).
/// </summary>
public sealed class HttpFetchLimits
{
    /// <summary>Default <c>User-Agent</c>: an explicit product agent, never a browser disguise.</summary>
    public const string DefaultUserAgent = "deepseek-harness/0.0.1 (+https://github.com/deepseek-ai)";

    /// <summary>Maximum response body size in bytes (the read is aborted past this).</summary>
    public long MaxResponseBytes { get; }

    /// <summary>Maximum decoded body length in characters (truncated past this).</summary>
    public int MaxBodyChars { get; }

    /// <summary>Default fetch timeout in milliseconds.</summary>
    public int TimeoutMs { get; }

    /// <summary>Maximum number of same-origin redirect hops to follow.</summary>
    public int MaxRedirects { get; }

    /// <summary><c>User-Agent</c> header sent on every request.</summary>
    public string UserAgent { get; }

    /// <summary>Create the limits with defaults applied; every bound is validated loudly.</summary>
    public HttpFetchLimits(
        long maxResponseBytes = 5_000_000,
        int maxBodyChars = 100_000,
        int timeoutMs = 30_000,
        int maxRedirects = 5,
        string userAgent = DefaultUserAgent)
    {
        if (maxResponseBytes <= 0 || maxResponseBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), "must be a positive number no greater than int.MaxValue");
        }
        if (maxBodyChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBodyChars), "must be a positive number");
        }
        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "must be a positive number");
        }
        if (maxRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRedirects), "must be a non-negative integer");
        }
        if (string.IsNullOrEmpty(userAgent))
        {
            throw new ArgumentException("must be a non-empty string", nameof(userAgent));
        }
        MaxResponseBytes = maxResponseBytes;
        MaxBodyChars = maxBodyChars;
        TimeoutMs = timeoutMs;
        MaxRedirects = maxRedirects;
        UserAgent = userAgent;
    }
}

/// <summary>
/// Anonymous public HTTP(S) fetch provider for the web seam. Validates and pins destinations to
/// http(s) with no embedded credentials, follows only same-origin redirects up to the hop cap,
/// enforces time and size limits, classifies and decodes text, and leaves presentation to the web
/// tools. Requests carry no browser cookies or ambient credentials. Port of
/// <c>@deepseek-ai/dsh-web-fetch-http</c> HttpFetchProvider.
/// </summary>
public sealed class HttpWebProvider : WebSeam.IFetchProvider
{
    /// <summary>Stable id this provider registers under.</summary>
    public const string LocalFetchProviderId = "http";

    /// <summary>Maximum accepted request URL length enforced before any network work.</summary>
    public const int WebFetchMaxUrlLength = 2048;

    /// <summary>The <c>Accept</c> header sent on every request.</summary>
    public const string AcceptHeader = "text/html,application/xhtml+xml,text/*;q=0.9,application/json;q=0.8";

    /// <summary>HTTP redirect status codes that carry a <c>Location</c>.</summary>
    private static readonly HashSet<int> RedirectStatuses = new() { 301, 302, 303, 307, 308 };

    private readonly HttpFetchLimits _limits;
    private readonly HttpClient _client;

    /// <summary>
    /// Create the provider. <paramref name="handler"/> is injectable so tests serve scripted
    /// responses with zero network; it defaults to the platform handler.
    /// </summary>
    public HttpWebProvider(HttpFetchLimits? limits = null, HttpMessageHandler? handler = null)
    {
        _limits = limits ?? new HttpFetchLimits();
        _client = new HttpClient(handler ?? new HttpClientHandler());
    }

    /// <inheritdoc />
    public string Id => LocalFetchProviderId;

    /// <summary>No credentials to check — an anonymous public fetcher is always usable.</summary>
    public bool Available() => true;

    /// <summary>
    /// Create the provider with an address pin: the named host resolves to the loopback port (the
    /// recorded fixture authority), everything else uses the platform handler. Used by the corpus
    /// web-fetch fixture so the recorded public.test URL reaches the embedded server.
    /// </summary>
    public static HttpWebProvider WithAddressPin(string host, int port, HttpFetchLimits? limits = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                var pinned = string.Equals(context.DnsEndPoint.Host, host, StringComparison.OrdinalIgnoreCase);
                await socket.ConnectAsync(pinned ? "127.0.0.1" : context.DnsEndPoint.Host, pinned ? port : context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return (Stream)new NetworkStream(socket, ownsSocket: true);
            },
        };
        return new HttpWebProvider(limits, handler);
    }

    /// <inheritdoc />
    /// <exception cref="WebError"><c>WEB_ABORTED</c> when already cancelled; <c>WEB_FETCH_TIMEOUT</c>,
    /// <c>WEB_ABORTED</c>, or <c>WEB_PROVIDER_ERROR</c> for transport-phase failures.</exception>
    public async Task<WebSeam.FetchResult> FetchAsync(WebSeam.FetchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new WebError("web fetch aborted", "WEB_ABORTED");
        }

        // One linked token stops both the request and the body read. The linked token firing with
        // the caller token still running is this provider's own timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_limits.TimeoutMs);
        return await FollowAndReadAsync(request.Url, cancellationToken, timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>Follow same-origin redirects up to the hop cap, then read the final response.</summary>
    private async Task<WebSeam.FetchResult> FollowAndReadAsync(string initialUrl, CancellationToken callerToken, CancellationToken linkToken)
    {
        var currentUrl = ValidateFetchUrl(initialUrl);
        var redirectsFollowed = 0;

        while (true)
        {
            using var response = await RequestOnceAsync(currentUrl, callerToken, linkToken).ConfigureAwait(false);
            if (!IsRedirectStatus((int)response.StatusCode))
            {
                return await ReadBodyAsync(response, currentUrl, callerToken, linkToken).ConfigureAwait(false);
            }

            // Enforce the redirect budget before resolving or validating the next hop.
            if (redirectsFollowed >= _limits.MaxRedirects)
            {
                throw new WebError($"exceeded the maximum of {_limits.MaxRedirects} redirects", "WEB_REDIRECT_BLOCKED");
            }
            var location = response.Headers.Location?.ToString();
            if (location is null)
            {
                // A redirect status with no Location is not a usable resource.
                throw new WebError($"redirect response (HTTP {(int)response.StatusCode}) without a Location header", "WEB_PROVIDER_ERROR");
            }

            var target = ResolveRedirect(location, currentUrl);
            // Re-validate the target against the same transport hygiene a direct request gets: a
            // redirect must not be a back door to a credentialed, non-http(s), or over-long URL.
            var validatedTarget = ValidateFetchUrl(target.ToString());
            if (!IsSameOrigin(validatedTarget, currentUrl))
            {
                throw new WebError(
                    $"cross-origin redirect to {validatedTarget.GetLeftPart(UriPartial.Authority)} is not followed automatically; retry against that URL directly",
                    "WEB_REDIRECT_BLOCKED");
            }
            currentUrl = validatedTarget;
            redirectsFollowed++;
        }
    }

    private async Task<HttpResponseMessage> RequestOnceAsync(Uri url, CancellationToken callerToken, CancellationToken linkToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _limits.UserAgent);
        request.Headers.Accept.ParseAdd(AcceptHeader);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Classified by which token fired, not by the thrown value.
            if (callerToken.IsCancellationRequested)
            {
                throw new WebError("web fetch aborted", "WEB_ABORTED");
            }
            throw new WebError("web fetch timed out", "WEB_FETCH_TIMEOUT");
        }
        catch (HttpRequestException error)
        {
            throw new WebError($"web fetch failed: {error.Message}", "WEB_PROVIDER_ERROR", error);
        }
    }

    /// <summary>Read, byte-cap, classify, and decode the final response body.</summary>
    private async Task<WebSeam.FetchResult> ReadBodyAsync(HttpResponseMessage response, Uri finalUrl, CancellationToken callerToken, CancellationToken linkToken)
    {
        var contentType = response.Content.Headers.ContentType?.ToString();
        var kind = ClassifyContentType(contentType);
        if (kind is null)
        {
            throw new WebError($"unsupported content type \"{contentType ?? "unknown"}\"", "WEB_UNSUPPORTED_CONTENT_TYPE");
        }

        // Resolve the decoder BEFORE reading the body so an unsupported charset fails without
        // consuming the stream.
        var encoding = DecoderForCharset(ParseCharset(contentType));
        var (bytes, truncatedByBytes) = await ReadCappedAsync(response, callerToken, linkToken).ConfigureAwait(false);
        var decoded = encoding.GetString(bytes);
        var truncatedByChars = decoded.Length > _limits.MaxBodyChars;
        var content = truncatedByChars ? decoded[.._limits.MaxBodyChars] : decoded;
        var body = kind == "html" ? (WebSeam.FetchBody)new WebSeam.HtmlBody(content) : new WebSeam.TextBody(content);

        return new WebSeam.FetchResult(finalUrl.ToString(), (int)response.StatusCode, body, truncatedByBytes || truncatedByChars);
    }

    /// <summary>
    /// Read the response stream up to <c>MaxResponseBytes</c>. A declared <c>Content-Length</c>
    /// over the cap rejects immediately with <c>WEB_FETCH_TOO_LARGE</c>; a stream that grows past
    /// the cap is cut short rather than rejected, so a server that under-reports still yields a
    /// bounded usable body. Only DROPPED bytes count as truncation: a body that exactly fills the
    /// cap is not falsely flagged.
    /// </summary>
    private async Task<(byte[] Bytes, bool TruncatedByBytes)> ReadCappedAsync(HttpResponseMessage response, CancellationToken callerToken, CancellationToken linkToken)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared is long length && length > _limits.MaxResponseBytes)
        {
            throw new WebError($"response exceeds the maximum of {_limits.MaxResponseBytes} bytes", "WEB_FETCH_TOO_LARGE");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(linkToken).ConfigureAwait(false);
        var chunks = new List<byte[]>();
        var total = 0L;
        var truncated = false;
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkToken).ConfigureAwait(false);
                if (read == 0) break;
                var remaining = _limits.MaxResponseBytes - total;
                if (read > remaining)
                {
                    var keep = new byte[checked((int)remaining)];
                    Array.Copy(buffer, keep, keep.Length);
                    chunks.Add(keep);
                    total += remaining;
                    truncated = true;
                    break;
                }
                var chunk = new byte[read];
                Array.Copy(buffer, chunk, read);
                chunks.Add(chunk);
                total += read;
            }
        }
        catch (OperationCanceledException)
        {
            // A read-phase abort is classified by which token fired, like the request phase.
            if (callerToken.IsCancellationRequested)
            {
                throw new WebError("web fetch aborted", "WEB_ABORTED");
            }
            if (linkToken.IsCancellationRequested)
            {
                throw new WebError("web fetch timed out", "WEB_FETCH_TIMEOUT");
            }
            throw new WebError("web fetch failed: the response stream was interrupted", "WEB_PROVIDER_ERROR");
        }
        catch (IOException error)
        {
            throw new WebError($"web fetch failed: {error.Message}", "WEB_PROVIDER_ERROR", error);
        }

        var bytes = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, bytes, offset, chunk.Length);
            offset += chunk.Length;
        }
        return (bytes, truncated);
    }

    /// <summary>Parse a request URL and enforce network-independent transport restrictions.</summary>
    /// <exception cref="WebError"><c>WEB_INVALID_URL</c> for an unparseable URL or non-http(s) scheme;
    /// <c>WEB_BLOCKED_URL</c> when the URL embeds credentials.</exception>
    public static Uri ParseFetchUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var url))
        {
            throw new WebError($"invalid URL: {input}", "WEB_INVALID_URL");
        }
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new WebError($"unsupported URL scheme \"{url.Scheme}:\" (only http and https are allowed)", "WEB_INVALID_URL");
        }
        if (url.UserInfo.Length > 0)
        {
            throw new WebError("credentials in URLs are not allowed", "WEB_BLOCKED_URL");
        }
        return url;
    }

    /// <summary>Validate a request URL against the complete pre-network policy: bounded length plus <see cref="ParseFetchUrl"/>.</summary>
    public static Uri ValidateFetchUrl(string input)
    {
        if (input.Length > WebFetchMaxUrlLength)
        {
            throw new WebError($"URL exceeds the maximum length of {WebFetchMaxUrlLength}", "WEB_INVALID_URL");
        }
        return ParseFetchUrl(input);
    }

    /// <summary>Two URLs are same-origin when scheme, host, and port match; a redirect that crosses origins is refused.</summary>
    public static bool IsSameOrigin(Uri a, Uri b)
        => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;

    private static bool IsRedirectStatus(int status) => RedirectStatuses.Contains(status);

    private static Uri ResolveRedirect(string location, Uri baseUrl)
    {
        try
        {
            return new Uri(baseUrl, location);
        }
        catch (Exception error)
        {
            throw new WebError($"invalid redirect Location \"{location}\"", "WEB_PROVIDER_ERROR", error);
        }
    }

    /// <summary>
    /// Classify a response <c>Content-Type</c> into a decodable body kind, or null for an
    /// unsupported (e.g. binary) type. <c>text/html</c> and <c>application/xhtml+xml</c> are html;
    /// other <c>text/*</c> plus a few structured text types are text.
    /// </summary>
    public static string? ClassifyContentType(string? contentType)
    {
        var mime = contentType ?? string.Empty;
        var semi = mime.IndexOf(';');
        if (semi >= 0) mime = mime[..semi];
        mime = mime.Trim().ToLowerInvariant();
        if (mime == "text/html" || mime == "application/xhtml+xml") return "html";
        if (mime.StartsWith("text/", StringComparison.Ordinal)) return "text";
        if (mime == "application/json" || mime == "application/xml"
            || mime.EndsWith("+json", StringComparison.Ordinal) || mime.EndsWith("+xml", StringComparison.Ordinal)) return "text";
        return null;
    }

    /// <summary>Extract the lower-cased <c>charset</c> parameter from a response <c>Content-Type</c>, or null when absent.</summary>
    public static string? ParseCharset(string? contentType)
    {
        if (contentType is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            contentType, @";\s*charset\s*=\s*""?([^"";]+)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : null;
    }

    /// <summary>
    /// Build a decoder for the declared charset, falling back to UTF-8 when none is declared.
    /// Throws <c>WEB_UNSUPPORTED_CONTENT_TYPE</c> when the label is present but not a recognized
    /// encoding — better to fail loudly than return mojibake.
    /// </summary>
    public static Encoding DecoderForCharset(string? charset)
    {
        return charset switch
        {
            null or "utf-8" or "utf8" => Encoding.UTF8,
            "ascii" or "us-ascii" => Encoding.ASCII,
            "latin1" or "iso-8859-1" => Encoding.Latin1,
            "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => throw new WebError($"unsupported charset \"{charset}\"", "WEB_UNSUPPORTED_CONTENT_TYPE"),
        };
    }
}


