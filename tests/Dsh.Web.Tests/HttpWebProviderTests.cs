using System.Net;
using System.Text;
using Dsh.Web;

namespace Dsh.Web.Tests;

/// <summary>
/// Transport behavior of <see cref="HttpWebProvider"/> against the fake handler: request shape,
/// status mapping, byte limits, redirect policy, and typed error mapping. Zero real network.
/// </summary>
public static class HttpWebProviderTests
{
    private const string Url = "https://example.com/page";

    public static void RequestShape_PinsMethodUrlAndHeaders()
    {
        var handler = new FakeHttpMessageHandler();
        var provider = new HttpWebProvider(new HttpFetchLimits(userAgent: "test-agent/1.0"), handler);
        provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();

        var captured = Assert.Single(handler.Captured);
        Assert.Equal("GET", captured.Method);
        Assert.Equal(Url, captured.Url);
        Assert.Equal("test-agent/1.0", captured.Headers["User-Agent"]);
        var accept = captured.Headers["Accept"];
        Assert.True(accept.Contains("text/html", StringComparison.Ordinal), accept);
        Assert.True(accept.Contains("application/json; q=0.8", StringComparison.Ordinal), accept);
    }

    public static void Http404_IsAResult_NotAnError()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Text("not found", HttpStatusCode.NotFound)) };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("not found", ((WebSeam.TextBody)result.Body).Content);
        Assert.False(result.Truncated);
    }

    public static void Http429_IsAResult_CarryingStatus()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Text("slow down", (HttpStatusCode)429)) };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("slow down", ((WebSeam.TextBody)result.Body).Content);
    }

    public static void Http500_IsAResult_CarryingStatus()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Text("boom", HttpStatusCode.InternalServerError)) };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("boom", ((WebSeam.TextBody)result.Body).Content);
    }

    public static void HtmlContent_ClassifiesHtml()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Html("<p>hello</p>")) };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        var body = Assert.IsType<WebSeam.HtmlBody>(result.Body);
        Assert.Equal("<p>hello</p>", body.Content);
        Assert.Equal("https://example.com/page", result.Url);
    }

    public static void JsonContent_ClassifiesText()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"),
            }),
        };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        var body = Assert.IsType<WebSeam.TextBody>(result.Body);
        Assert.Equal("{\"a\":1}", body.Content);
    }

    public static void UnsupportedContentType_Throws()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            }),
        };
        var provider = new HttpWebProvider(handler: handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url)));
        Assert.Equal("WEB_UNSUPPORTED_CONTENT_TYPE", error.Code);
    }

    public static void UnsupportedCharset_Throws()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Bytes(
                HttpStatusCode.OK, Encoding.UTF8.GetBytes("caf\u00e9"), "text/plain", "x-made-up")),
        };
        var provider = new HttpWebProvider(handler: handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url)));
        Assert.Equal("WEB_UNSUPPORTED_CONTENT_TYPE", error.Code);
    }

    public static void DeclaredContentLength_OverCap_RejectsImmediately()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Bytes(HttpStatusCode.OK, new byte[200], "text/plain")),
        };
        var provider = new HttpWebProvider(new HttpFetchLimits(maxResponseBytes: 100), handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url)));
        Assert.Equal("WEB_FETCH_TOO_LARGE", error.Code);
        Assert.True(error.Message.Contains("100", StringComparison.Ordinal));
    }

    public static void Stream_GrowingPastCap_IsTruncated_NotRejected()
    {
        var body = new string('x', 150);
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Stream(new NonSeekableStream(Encoding.UTF8.GetBytes(body)), "text/plain")),
        };
        var provider = new HttpWebProvider(new HttpFetchLimits(maxResponseBytes: 100, maxBodyChars: 1000), handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.True(result.Truncated);
        var content = ((WebSeam.TextBody)result.Body).Content;
        Assert.Equal(100, content.Length);
        Assert.Equal(new string('x', 100), content);
    }

    public static void Stream_ExactlyAtCap_IsNotFlaggedTruncated()
    {
        var body = new string('y', 100);
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Text(body)) };
        var provider = new HttpWebProvider(new HttpFetchLimits(maxResponseBytes: 100, maxBodyChars: 1000), handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.False(result.Truncated);
        Assert.Equal(100, ((WebSeam.TextBody)result.Body).Content.Length);
    }

    public static void DecodedBody_OverCharCap_IsTruncated()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Text("hello world")) };
        var provider = new HttpWebProvider(new HttpFetchLimits(maxBodyChars: 5), handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.True(result.Truncated);
        Assert.Equal("hello", ((WebSeam.TextBody)result.Body).Content);
    }

    public static void NonHttpScheme_ThrowsInvalidUrl()
    {
        var provider = new HttpWebProvider(handler: new FakeHttpMessageHandler());
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest("ftp://example.com/file")));
        Assert.Equal("WEB_INVALID_URL", error.Code);
    }

    public static void CredentialsInUrl_ThrowsBlocked()
    {
        var provider = new HttpWebProvider(handler: new FakeHttpMessageHandler());
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest("http://user:pass@example.com/")));
        Assert.Equal("WEB_BLOCKED_URL", error.Code);
    }

    public static void OverlongUrl_ThrowsInvalidUrl()
    {
        var provider = new HttpWebProvider(handler: new FakeHttpMessageHandler());
        var longUrl = "https://example.com/" + new string('a', HttpWebProvider.WebFetchMaxUrlLength);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(longUrl)));
        Assert.Equal("WEB_INVALID_URL", error.Code);
    }

    public static void SameOriginRedirect_IsFollowed()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/old")
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("/new", UriKind.Relative);
                    return Task.FromResult(response);
                }
                return Task.FromResult(Responses.Html("<p>landed</p>"));
            },
        };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest("https://example.com/old")).GetAwaiter().GetResult();
        Assert.Equal(2, handler.Captured.Count);
        Assert.Equal("https://example.com/new", result.Url);
        Assert.Equal("<p>landed</p>", ((WebSeam.HtmlBody)result.Body).Content);
    }

    public static void CrossOriginRedirect_IsBlocked()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://evil.example/steal");
                return Task.FromResult(response);
            },
        };
        var provider = new HttpWebProvider(handler: handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest("https://example.com/old")));
        Assert.Equal("WEB_REDIRECT_BLOCKED", error.Code);
        Assert.True(error.Message.Contains("evil.example", StringComparison.Ordinal));
        Assert.Equal(1, handler.Captured.Count);
    }

    public static void RedirectBudget_IsEnforced()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Responder = (request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("/hop" + handler.Captured.Count, UriKind.Relative);
            return Task.FromResult(response);
        };
        var provider = new HttpWebProvider(new HttpFetchLimits(maxRedirects: 1), handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest("https://example.com/start")));
        Assert.Equal("WEB_REDIRECT_BLOCKED", error.Code);
        Assert.True(error.Message.Contains("1", StringComparison.Ordinal));
        Assert.Equal(2, handler.Captured.Count);
    }

    public static void RedirectWithoutLocation_ThrowsProviderError()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)),
        };
        var provider = new HttpWebProvider(handler: handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url)));
        Assert.Equal("WEB_PROVIDER_ERROR", error.Code);
        Assert.True(error.Message.Contains("Location", StringComparison.Ordinal));
    }

    public static void PreCancelled_ThrowsAborted()
    {
        var provider = new HttpWebProvider(handler: new FakeHttpMessageHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url), cts.Token));
        Assert.Equal("WEB_ABORTED", error.Code);
    }

    public static void TransportFailure_ThrowsProviderError()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => throw new HttpRequestException("connection refused"),
        };
        var provider = new HttpWebProvider(handler: handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url)));
        Assert.Equal("WEB_PROVIDER_ERROR", error.Code);
        Assert.True(error.Message.Contains("connection refused", StringComparison.Ordinal));
    }

    public static void Timeout_ThrowsFetchTimeout()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var provider = new HttpWebProvider(new HttpFetchLimits(timeoutMs: 50), handler);
        var error = Assert.ThrowsAny<WebError>(() => provider.FetchAsync(new WebSeam.FetchRequest(Url)));
        Assert.Equal("WEB_FETCH_TIMEOUT", error.Code);
    }

    public static void CharsetDeclaration_IsDecoded()
    {
        // iso-8859-1 byte 0xE9 is "é"; decoded through the declared charset rather than UTF-8.
        var bytes = new byte[] { 0x63, 0x61, 0x66, 0xE9 }; // "café" in latin1
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Bytes(HttpStatusCode.OK, bytes, "text/plain", "iso-8859-1")),
        };
        var provider = new HttpWebProvider(handler: handler);
        var result = provider.FetchAsync(new WebSeam.FetchRequest(Url)).GetAwaiter().GetResult();
        Assert.Equal("caf\u00e9", ((WebSeam.TextBody)result.Body).Content);
    }
}



