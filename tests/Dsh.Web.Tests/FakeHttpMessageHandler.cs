using System.Net;
using System.Text;

namespace Harness.Web.Tests;

/// <summary>One request's facts, captured at send time so nothing is read after the provider disposes the request.</summary>
internal sealed record CapturedRequest(string Method, string? Url, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// In-memory HttpMessageHandler: records every request and serves scripted responses. No network
/// is ever touched, so the suite runs offline.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; }
        = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
        });

    public List<CapturedRequest> Captured { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Headers)
        {
            headers[pair.Key] = string.Join(", ", pair.Value);
        }
        Captured.Add(new CapturedRequest(request.Method.ToString(), request.RequestUri?.ToString(), headers));
        return Responder(request, cancellationToken);
    }
}

/// <summary>
/// A stream whose length cannot be known up front (CanSeek is false), so StreamContent serves it
/// without a Content-Length header and the provider's byte cap runs while reading.
/// </summary>
internal sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;

    public NonSeekableStream(byte[] bytes)
    {
        _inner = new MemoryStream(bytes);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Scripted response builders for the fake handler.</summary>
internal static class Responses
{
    /// <summary>A 200 text/plain response with the given body.</summary>
    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

    /// <summary>A 200 text/html response with the given body.</summary>
    public static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };

    /// <summary>A response with the given status and a raw body of the given bytes; the charset rides the media-type parameter.</summary>
    public static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body, string contentType, string? charset = null)
    {
        var header = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (charset is not null) header.CharSet = charset;
        return new(status)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = header } },
        };
    }

    /// <summary>
    /// A response whose body is served from a stream without a declared <c>Content-Length</c>, so
    /// the byte cap is enforced while reading rather than from the declared length.
    /// </summary>
    public static HttpResponseMessage Stream(Stream body, string contentType)
        => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(body) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } },
        };
}



