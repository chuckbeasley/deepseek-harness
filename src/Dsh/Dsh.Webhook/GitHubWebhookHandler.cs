using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Credentials;

namespace Harness.Webhook;

/// <summary>
/// HTTP refusal whose message is safe to return without request data. The status vocabulary is
/// closed: 400 (malformed request), 401 (bad signature), 405 (wrong method), 413 (oversized
/// body), 415 (wrong content type), 503 (unavailable dependency).
/// </summary>
public sealed class WebhookHttpError : Exception
{
    /// <summary>Create the refusal.</summary>
    public WebhookHttpError(int status, string message)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The HTTP status to answer with.</summary>
    public int Status { get; }
}

/// <summary>One decoded inbound HTTP request handed to a webhook handler.</summary>
public sealed record WebhookHttpRequest(
    /// <summary>The request method, upper-cased by the caller.</summary>
    string Method,
    /// <summary>All header values, keyed case-insensitively.</summary>
    IReadOnlyDictionary<string, string[]> Headers,
    /// <summary>The exact request body bytes, already read to EOF.</summary>
    byte[] Body,
    /// <summary>The raw <c>content-type</c> header, or <c>null</c>.</summary>
    string? ContentType = null);

/// <summary>The exact HTTP answer of one handled request.</summary>
public sealed record WebhookHttpResponse(
    /// <summary>The status to answer with.</summary>
    int Status,
    /// <summary>Optional plain-text body.</summary>
    string? Body = null,
    /// <summary>Optional <c>allow</c> header for method refusals.</summary>
    string? Allow = null);

/// <summary>Handler configuration validated once at construction.</summary>
public sealed record GitHubWebhookHandlerConfig(
    /// <summary>Adapter instance name surfaced as delivery provenance.</summary>
    WebhookSourceId Source,
    /// <summary>Credential reference resolving the shared HMAC secret.</summary>
    string SecretRef,
    /// <summary>Positive byte ceiling for request bodies.</summary>
    int MaxBodyBytes);

/// <summary>
/// The GitHub webhook consumer (port of <c>@deepseek-ai/dsh-webhook-github</c>): HTTP
/// authentication, parsing, and fire-and-forget dispatch for one GitHub-style delivery. Pure
/// over <see cref="WebhookHttpRequest"/> — no sockets — so the protocol is fully testable; the
/// ingress provider bridges real HTTP onto it. Verification is HMAC-SHA256 over the exact body
/// bytes with a constant-time compare.
/// </summary>
public sealed class GitHubWebhookHandler
{
    private readonly Context _ctx;
    private readonly IWebhookService _webhook;
    private readonly ICredentialsService _credentials;
    private readonly GitHubWebhookHandlerConfig _config;

    /// <summary>Create the handler over the webhook runtime and the credentials service.</summary>
    public GitHubWebhookHandler(Context ctx, IWebhookService webhook, ICredentialsService credentials, GitHubWebhookHandlerConfig config)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _webhook = webhook ?? throw new ArgumentNullException(nameof(webhook));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.MaxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "maxBodyBytes must be positive");
        }
    }

    /// <summary>
    /// Handle one request and answer after in-memory dispatch, never rule settlement.
    /// </summary>
    /// <param name="request">the decoded inbound request.</param>
    /// <returns>the exact answer; failures map to their <see cref="WebhookHttpError"/> statuses.</returns>
    public async Task<WebhookHttpResponse> HandleAsync(WebhookHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Method != "POST")
            {
                return new WebhookHttpResponse(405, "method not allowed", Allow: "POST");
            }
            if (!IsJsonContentType(request.ContentType))
            {
                return new WebhookHttpResponse(415, "content type must be application/json");
            }
            var raw = request.Body;
            if (raw.Length > _config.MaxBodyBytes)
            {
                return new WebhookHttpResponse(413, "request body is too large");
            }
            string body;
            try
            {
                body = StrictUtf8.GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                return new WebhookHttpResponse(400, "request body is not valid UTF-8");
            }
            var signature = RequiredHeader(request, "x-hub-signature-256");
            var deliveryId = RequiredHeader(request, "x-github-delivery");
            var eventName = RequiredHeader(request, "x-github-event");
            var credential = await _credentials.ResolveAsync(_config.SecretRef).ConfigureAwait(false);
            if (credential is null || credential.Value.Length == 0)
            {
                return new WebhookHttpResponse(503, "GitHub webhook secret is unavailable");
            }
            if (!VerifySignature(credential.Value, raw, signature))
            {
                return new WebhookHttpResponse(401, "invalid webhook signature");
            }
            var payload = ParsePayload(body);
            var delivery = new VerifiedWebhookDelivery(
                Kind: "github",
                Source: _config.Source,
                DeliveryId: new WebhookDeliveryId(deliveryId),
                Event: JsonSerializer.SerializeToElement(new GitHubWebhookEvent(eventName, payload), WireJson),
                ReceivedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                _webhook.Dispatch(delivery);
            }
            catch (Exception error)
            {
                _ctx.Logger.Warn($"webhook-github: dispatch unavailable: {error.Message}");
                return new WebhookHttpResponse(503, "webhook runtime is unavailable");
            }
            return new WebhookHttpResponse(202);
        }
        catch (WebhookHttpError error)
        {
            return new WebhookHttpResponse(error.Status, error.Message);
        }
        catch (Exception error)
        {
            _ctx.Logger.Warn($"webhook-github: request failed: {error.Message}");
            return new WebhookHttpResponse(503, "webhook ingress is unavailable");
        }
    }

    /// <summary>Require one unambiguous non-empty request header.</summary>
    private static string RequiredHeader(WebhookHttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out var values) || values.Length != 1 || values[0].Trim().Length == 0)
        {
            throw new WebhookHttpError(400, $"missing {name} header");
        }
        return values[0];
    }

    /// <summary>Whether Content-Type names JSON with at most one UTF-8 charset parameter.</summary>
    private static bool IsJsonContentType(string? value)
    {
        if (value is null) return false;
        var parts = value.Split(';').Select(part => part.Trim()).ToArray();
        if (!string.Equals(parts[0], "application/json", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length == 1) return true;
        if (parts.Length > 2) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^charset=(?:utf-8|\"utf-8\")$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Camel-cased wire JSON for the provider-normalized event (TS wire spelling).</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Strict UTF-8 decoding; a decoding failure means the body is not valid UTF-8.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Verify the <c>sha256=&lt;hex&gt;</c> signature over the exact body bytes.</summary>
    private static bool VerifySignature(string secret, byte[] body, string signature)
    {
        if (!signature.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        var expected = signature["sha256=".Length..];
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        byte[] actual;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
        {
            actual = hmac.ComputeHash(body);
        }
        var actualHex = Convert.ToHexStringLower(actual);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualHex),
            Encoding.ASCII.GetBytes(expected.ToLowerInvariant()));
    }

    /// <summary>Convert a parsed value into the adapter generic signed-object guarantee.</summary>
    private static JsonElement ParsePayload(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new WebhookHttpError(400, "request body is not valid JSON");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new WebhookHttpError(400, "GitHub webhook payload must be a JSON object");
            }
            return root.Clone();
        }
    }
}
