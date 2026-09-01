using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Session;

namespace Harness.Web;

/// <summary>
/// The durable secret-free DeepSeek Messages request recorded immediately before one auxiliary
/// search dispatch (the recorded <c>web/deepseek-search-llm-request</c> session event).
/// </summary>
public sealed record WebDeepSeekSearchLlmRequestEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "web/deepseek-search-llm-request";

    /// <summary>Fully resolved Messages endpoint.</summary>
    public required string Endpoint { get; init; }

    /// <summary><c>anthropic-version</c> header value.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>The exact JSON body sent to the provider.</summary>
    public required JsonElement Body { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the web/* event types into the session registry.</summary>
public static class WebEventTypes
{
    /// <summary>Register the search-request discriminator; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(WebDeepSeekSearchLlmRequestEvent.EventTypeName, typeof(WebDeepSeekSearchLlmRequestEvent));
    }
}

/// <summary>
/// DeepSeek search through an Anthropic-compatible Messages model call with the native
/// <c>web_search_20250305</c> server tool (port of <c>@deepseek-ai/dsh-web-search-deepseek</c>).
/// The exact secret-free request is recorded as the <c>web/deepseek-search-llm-request</c> session
/// event on the caller's session before dispatch; failures after dispatch name the endpoint and
/// tell the model how the user can configure it.
/// </summary>
public sealed class DeepSeekSearchProvider : WebSeam.ISearchProvider
{
    /// <summary>Stable id this provider registers under.</summary>
    public const string ProviderId = "deepseek-official";

    /// <summary>Default endpoint base: DeepSeek's Anthropic-compatible API (<c>/messages</c> is appended).</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/anthropic/v1";

    /// <summary>Default Anthropic-format model name.</summary>
    public const string DefaultModel = "deepseek-v4-flash";

    /// <summary>Default <c>anthropic-version</c> header value.</summary>
    public const string DefaultApiVersion = "2023-06-01";

    /// <summary>Default upper bound on generated tokens for the Messages request.</summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>Default maximum <c>web_search</c> server-tool uses per request.</summary>
    public const int DefaultMaxUses = 5;

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiVersion;
    private readonly int _maxTokens;
    private readonly int _maxUses;
    private readonly HttpClient _client;

    /// <summary>Create the provider.</summary>
    public DeepSeekSearchProvider(
        string apiKey,
        string baseUrl,
        string model = DefaultModel,
        string apiVersion = DefaultApiVersion,
        int maxTokens = DefaultMaxTokens,
        int maxUses = DefaultMaxUses,
        HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _model = model;
        _apiVersion = apiVersion;
        _maxTokens = maxTokens;
        _maxUses = maxUses;
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public bool Available()
        => _apiKey.Length > 0
            && Uri.TryCreate(_baseUrl, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            && _maxTokens > 0
            && _maxUses > 0;

    /// <inheritdoc />
    public async Task<WebSeam.SearchResult> SearchAsync(WebSeam.SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = _baseUrl + "/messages";
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = _maxTokens,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Perform a web search for the query: {request.Query}",
                }),
            }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search_20250305",
                ["name"] = "web_search",
                ["max_uses"] = _maxUses,
            }),
        };
        // Record the exact secret-free request durably before dispatch so model-visible auxiliary
        // input cannot escape logging (the recorded web/deepseek-search-llm-request event).
        request.Session?.Append(new WebDeepSeekSearchLlmRequestEvent
        {
            Endpoint = endpoint,
            ApiVersion = _apiVersion,
            Body = JsonSerializer.SerializeToElement(body),
        });
        cancellationToken.ThrowIfCancellationRequested();

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        message.Headers.TryAddWithoutValidation("authorization", "Bearer " + _apiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", _apiVersion);
        message.Headers.TryAddWithoutValidation("user-agent", "deepseek-harness/0.0.1");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new WebError("DeepSeek search aborted", "WEB_ABORTED");
        }
        catch (HttpRequestException error)
        {
            throw SearchEndpointError(endpoint, $"DeepSeek search request failed: {error.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
                var messageText = $"DeepSeek API error (HTTP {(int)response.StatusCode})";
                if (detail is not null) messageText += $": {detail}";
                throw SearchEndpointError(endpoint, messageText);
            }
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapAnthropicResponse(JsonDocument.Parse(payload).RootElement, cancellationToken);
        }
    }

    /// <summary>Read the <c>error.message</c> (or string <c>error</c>) detail of a non-ok response, if any.</summary>
    private static async Task<string?> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String && message.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
            if (root.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String)
            {
                return top.GetString();
            }
        }
        catch (Exception)
        {
            // A malformed or non-JSON error body costs only a richer message, never the real error.
        }
        return null;
    }

    /// <summary>Map a DeepSeek Anthropic Messages response to a normalized search result (port of mapAnthropicResponse).</summary>
    internal static WebSeam.SearchResult MapAnthropicResponse(JsonElement response, CancellationToken cancellationToken = default)
    {
        var blocks = response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var resultBlocks = blocks.Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "web_search_tool_result").ToArray();
        if (resultBlocks.Length == 0)
        {
            throw new WebError(
                "DeepSeek returned no web_search_tool_result blocks; the request may not have triggered native web search",
                "WEB_PROVIDER_ERROR");
        }
        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (!block.TryGetProperty("type", out var type) || type.GetString() != "text") continue;
            if (!block.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array) continue;
            foreach (var cite in citations.EnumerateArray())
            {
                var url = cite.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
                var citedText = cite.TryGetProperty("cited_text", out var citedValue) ? citedValue.GetString() : null;
                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(citedText) && !snippets.ContainsKey(url))
                {
                    snippets[url] = citedText;
                }
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<WebSeam.SearchSource>();
        foreach (var block in resultBlocks)
        {
            if (!block.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "web_search_result") continue;
                var url = item.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
                if (string.IsNullOrEmpty(url) || !seen.Add(url)) continue;
                var title = item.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
                var pageAge = item.TryGetProperty("page_age", out var ageValue) ? ageValue.GetString() : null;
                snippets.TryGetValue(url, out var snippet);
                sources.Add(new WebSeam.SearchSource(
                    url,
                    string.IsNullOrEmpty(title) ? null : title,
                    string.IsNullOrEmpty(snippet) ? null : snippet,
                    string.IsNullOrEmpty(pageAge) ? null : pageAge));
            }
        }
        return new WebSeam.SearchResult(null, sources.ToArray(), false);
    }

    /// <summary>Add endpoint recovery instructions to a failure that occurred after request dispatch began.</summary>
    private static WebError SearchEndpointError(string endpoint, string message)
        => new(
            $"{message}\n\nThe web search request used endpoint {JsonSerializer.Serialize(endpoint)}. "
            + "Search endpoint configuration is separate from chat. If that endpoint is not intended, "
            + "guide the user to Settings > Plugins > Plugin configuration > Web search, where they can "
            + "change and save Endpoint. If that settings page is unavailable, the user can set "
            + "DEEPSEEK_SEARCH_BASE_URL or configure web-search-deepseek.baseURL to a trusted "
            + "Anthropic-compatible Messages API base. Only the user should choose or change the endpoint.",
            "WEB_PROVIDER_ERROR");
}