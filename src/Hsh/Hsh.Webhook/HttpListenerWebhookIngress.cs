using System.Net;
using Harness.Cordis.Core;

namespace Harness.Webhook;

/// <summary>Service Definition of the webhook ingress: one HTTP listener that bridges real requests onto a handler.</summary>
public interface IWebhookIngress
{
    /// <summary>The prefix this ingress listens on.</summary>
    string Prefix { get; }

    /// <summary>Start listening and begin the accept loop.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop listening and await the accept loop exit.</summary>
    Task StopAsync();
}

/// <summary>Configuration for the loopback-capable HTTP listener ingress.</summary>
public sealed record HttpListenerWebhookIngressConfig(
    /// <summary>The <see cref="HttpListener"/> prefix (for example <c>http://127.0.0.1:8080/webhook/</c>).</summary>
    string Prefix,
    /// <summary>The handler every request is routed to.</summary>
    Func<WebhookHttpRequest, Task<WebhookHttpResponse>> Handler,
    /// <summary>Positive byte ceiling enforced while draining request bodies.</summary>
    int MaxBodyBytes);

/// <summary>
/// The local HTTP webhook ingress (ctx.webhookIngress): an <see cref="HttpListener"/> owned by
/// the seam, routing every request to the configured handler. The handler owns the protocol —
/// this provider only drains a bounded body, maps the request, and writes the exact answer.
/// </summary>
public sealed class HttpListenerWebhookIngress : Service, IWebhookIngress
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "webhookIngress";

    private readonly HttpListenerWebhookIngressConfig _config;
    private readonly HttpListener _listener = new();
    private Task? _acceptLoop;
    private readonly object _gate = new();
    private bool _stopped;

    /// <summary>Create and register the ingress under the <c>webhookIngress</c> key.</summary>
    public HttpListenerWebhookIngress(Context ctx, HttpListenerWebhookIngressConfig config)
        : base(ctx, ServiceKey)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.Prefix.Length == 0) throw new ArgumentException("prefix must be non-empty", nameof(config));
        if (config.Handler is null) throw new ArgumentException("handler must be provided", nameof(config));
        if (config.MaxBodyBytes <= 0) throw new ArgumentOutOfRangeException(nameof(config), "maxBodyBytes must be positive");
    }

    /// <inheritdoc />
    public string Prefix => _config.Prefix;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_acceptLoop is not null) return Task.CompletedTask;
            _listener.Prefixes.Add(_config.Prefix);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            loop = _acceptLoop;
        }
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // the listener was already closed
        }
        _listener.Close();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // the stop that closed the listener is the loop exit
            }
        }
    }

    /// <summary>Accept loop: handle every context fire-and-forget until the listener closes.</summary>
    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                return; // listener closed
            }
            _ = HandleContainedAsync(context);
        }
    }

    /// <summary>One contained request: drain, route, answer. A broken handler still answers 503.</summary>
    private async Task HandleContainedAsync(HttpListenerContext context)
    {
        try
        {
            var request = await ReadRequestAsync(context.Request).ConfigureAwait(false);
            if (request is null)
            {
                await AnswerAsync(context.Response, new WebhookHttpResponse(413, "request body is too large")).ConfigureAwait(false);
                return;
            }
            var response = await _config.Handler(request).ConfigureAwait(false);
            await AnswerAsync(context.Response, response).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"webhookIngress: request failed: {error.Message}");
            try
            {
                await AnswerAsync(context.Response, new WebhookHttpResponse(503, "webhook ingress is unavailable")).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the connection died mid-answer; nothing more to do
            }
        }
    }

    /// <summary>Drain the request body bounded by the configured ceiling; <c>null</c> when over it.</summary>
    private async Task<WebhookHttpRequest?> ReadRequestAsync(HttpListenerRequest request)
    {
        byte[] body;
        using (var memory = new MemoryStream())
        {
            var buffer = new byte[8192];
            var total = 0;
            while (true)
            {
                var read = await request.InputStream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > _config.MaxBodyBytes) return null;
                memory.Write(buffer, 0, read);
            }
            body = memory.ToArray();
        }
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Headers.AllKeys)
        {
            if (name is null) continue;
            headers[name] = request.Headers.GetValues(name) ?? Array.Empty<string>();
        }
        return new WebhookHttpRequest(
            request.HttpMethod,
            headers,
            body,
            request.ContentType);
    }

    /// <summary>Write one exact answer.</summary>
    private static async Task AnswerAsync(HttpListenerResponse response, WebhookHttpResponse answer)
    {
        response.StatusCode = answer.Status;
        if (answer.Allow is not null) response.AddHeader("Allow", answer.Allow);
        if (answer.Body is null)
        {
            response.ContentLength64 = 0;
            response.Close();
            return;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(answer.Body);
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}
