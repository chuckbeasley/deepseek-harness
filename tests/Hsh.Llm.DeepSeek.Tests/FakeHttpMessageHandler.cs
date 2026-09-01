using System.Net;
using System.Text;

namespace Harness.Llm.DeepSeek.Tests;

/// <summary>
/// In-memory HttpMessageHandler: records every request and serves scripted responses. No network
/// is ever touched, so the suite runs without an API key.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; }
        = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Request bodies captured at send time; the adapter disposes the request afterwards.</summary>
    public List<string> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        return await Responder(request, cancellationToken);
    }
}

/// <summary>Scripted response builders for the fake handler.</summary>
internal static class Responses
{
    /// <summary>One SSE 200 response whose <c>data:</c> frames carry the given payloads (newlines become continuation lines).</summary>
    public static HttpResponseMessage Sse(params string[] payloads)
    {
        var builder = new StringBuilder();
        foreach (var payload in payloads)
        {
            builder.Append("data: ").Append(payload.Replace("\n", "\ndata: ")).Append("\n\n");
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    /// <summary>One JSON error response at the given status.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string jsonBody)
        => new(status)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
}

/// <summary>
/// Stream that serves a fixed byte prefix then blocks forever (until cancelled), used to make
/// mid-stream cancellation deterministic: the prefix ends before <c>[DONE]</c>, so the next read
/// parks on the cancellation token.
/// </summary>
internal sealed class SlowSseStream : Stream
{
    private readonly byte[] _bytes;
    private int _position;

    public SlowSseStream(string sse)
    {
        _bytes = Encoding.UTF8.GetBytes(sse);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _bytes.Length)
        {
            var count = Math.Min(buffer.Length, _bytes.Length - _position);
            _bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        // No [DONE] is ever served: park on the token so a cancelled read surfaces OCE.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
