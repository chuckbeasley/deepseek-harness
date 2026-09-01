using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Llm.DeepSeek;

/// <summary>
/// Chat-completions adapter for DeepSeek (OpenAI-compatible endpoint). Builds the request from
/// GenerateOptions, streams the SSE response into the harness StreamChunk vocabulary, and maps
/// HTTP and API errors into <see cref="LlmError"/> with provider-neutral codes. Connection facts
/// resolve per request: the API key comes from config, then <c>DEEPSEEK_API_KEY</c>; the endpoint
/// from config, then <c>DEEPSEEK_BASE_URL</c>, then the public API. Cancellation aborts the
/// outstanding read and surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DeepSeekAdapter : ILlmAdapter
{
    /// <summary>Public DeepSeek endpoint; the internal endpoint comes from config or $DEEPSEEK_BASE_URL.</summary>
    public const string PublicBaseUrl = "https://api.deepseek.com";

    /// <summary>Environment variable naming the provider API key.</summary>
    public const string ApiKeyEnvVar = "DEEPSEEK_API_KEY";

    /// <summary>Environment variable naming the provider endpoint base.</summary>
    public const string BaseUrlEnvVar = "DEEPSEEK_BASE_URL";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeepSeekConfig _config;
    private readonly HttpClient _client;

    /// <summary>
    /// Create the adapter. Connection facts are read per request so a configuration change reaches
    /// the next call; the cross-field thinking/effort contract is validated here (load time).
    /// </summary>
    /// <param name="config">validated connection facts; see <see cref="DeepSeekConfig"/>.</param>
    /// <param name="handler">injectable handler for tests; defaults to the platform handler.</param>
    public DeepSeekAdapter(DeepSeekConfig config, HttpMessageHandler? handler = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.ApiKeyEnv is { Length: 0 })
        {
            throw new LlmError("DeepSeek config ApiKeyEnv must not be empty", "INVALID_CONFIG");
        }
        if (config.BaseUrl is not null && !IsAbsoluteHttpUrl(config.BaseUrl))
        {
            throw new LlmError($"DeepSeek config BaseUrl must be an absolute http(s) URL, got \"{config.BaseUrl}\"", "INVALID_CONFIG");
        }
        if (config.Thinking == false && config.ReasoningEffort is DeepSeekReasoningEffort.Low or DeepSeekReasoningEffort.High or DeepSeekReasoningEffort.Max)
        {
            throw new LlmError($"DeepSeek deployment does not support reasoning effort \"{config.ReasoningEffort}\"", "UNSUPPORTED_REASONING_EFFORT");
        }
        _client = new HttpClient(handler ?? new HttpClientHandler());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var apiKey = ResolveApiKey();
        var baseUrl = ResolveBaseUrl();

        var body = JsonSerializer.Serialize(RequestSerializer.Serialize(request, _config), SerializerOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LlmError($"DeepSeek API request to {baseUrl} failed", "TRANSPORT");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ErrorFromResponseAsync(response, ct).ConfigureAwait(false);
            }
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var chunk in StreamResponse(stream, baseUrl, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>Stream the SSE body, remapping non-LlmError transport failures to <c>TRANSPORT</c>.</summary>
#pragma warning disable CS8425 // the token is captured at call time; GetAsyncEnumerator supplies none
    private static async IAsyncEnumerable<StreamChunk> StreamResponse(Stream stream, string baseUrl, CancellationToken ct)
#pragma warning restore CS8425
    {
        var enumerator = Translate.Run(SseParser.ParseAsync(stream, ct)).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (LlmError)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw new LlmError($"DeepSeek API stream from {baseUrl} failed", "TRANSPORT");
                }
                if (!moved) break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Build the LlmError for a non-2xx response: the provider message wins when the body carries
    /// one, the HTTP status remains authoritative when the body is malformed JSON, and the code
    /// comes from <see cref="ErrorMapping.HttpErrorCode"/>.
    /// </summary>
    private static async Task<LlmError> ErrorFromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        WireError? error = null;
        var message = $"DeepSeek API error (HTTP {status})";
        try
        {
            error = JsonSerializer.Deserialize<WireError>(raw);
            if (error?.Error?.Message is { Length: > 0 } providerMessage) message = providerMessage;
        }
        catch (JsonException)
        {
            // The HTTP status remains authoritative when a gateway returns malformed JSON.
        }
        return new LlmError(message, ErrorMapping.HttpErrorCode(status, error), status);
    }

    /// <summary>Resolve the bearer token: config, then the configured (or default) environment variable.</summary>
    private string ResolveApiKey()
    {
        if (!string.IsNullOrEmpty(_config.ApiKey)) return _config.ApiKey;
        var envName = _config.ApiKeyEnv ?? ApiKeyEnvVar;
        var fromEnv = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        throw new LlmError("DeepSeek API key is not configured", "MISSING_CREDENTIAL");
    }

    /// <summary>Resolve the endpoint base: config, then $DEEPSEEK_BASE_URL, then the public API.</summary>
    private string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_config.BaseUrl)) return _config.BaseUrl.TrimEnd('/');
        var fromEnv = Environment.GetEnvironmentVariable(BaseUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.TrimEnd('/');
        return PublicBaseUrl;
    }

    /// <summary>True when the value is an absolute http(s) URL.</summary>
    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
