using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;

namespace Dsh.Llm.DeepSeek.Tests;

/// <summary>Behavior tests for the DeepSeek adapter against a fake HttpMessageHandler (no network).</summary>
public static class AdapterTests
{
    private static GenerateOptions Request(
        IReadOnlyList<Message>? messages = null,
        string? system = null,
        IReadOnlyList<ToolSchema>? tools = null,
        double? temperature = null,
        int? maxTokens = null)
        => new("deepseek", "deepseek-chat", messages ?? Array.Empty<Message>(), system, tools, temperature, maxTokens);

    private static void Drain(DeepSeekAdapter adapter, GenerateOptions request)
    {
        var enumerator = adapter.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        while (true)
        {
            if (!enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) break;
        }
    }

    private static JsonNode Body(FakeHttpMessageHandler handler)
    {
        var json = Assert.Single(handler.RequestBodies);
        return JsonNode.Parse(json)!;
    }

    private static LlmError CaptureAdapterError(DeepSeekAdapter adapter, GenerateOptions request)
    {
        var enumerator = adapter.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                var moved = enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
                if (!moved) break;
            }
        }
        catch (LlmError error)
        {
            return error;
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        throw new AssertionException("expected LlmError but the stream completed");
    }

    private static void SetEnv(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    public static void RequestShape_PinsUrlAuthHeadersAndBody()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test-123", BaseUrl: "https://api.deepseek.com"), handler);
        var messages = new Message[]
        {
            Messages.CreateUserMessage(new ContentBlock[] { new TextBlock("Hello") }),
            Messages.CreateAssistantMessage("deepseek", "deepseek-chat", new ContentBlock[]
            {
                new ReasoningBlock("thinking..."),
                new TextBlock("Hi!"),
                new ToolCallBlock(new ToolCallId("call_1"), "todo_write", "{\"todos\":[]}"),
            }),
            ToolResultMessage.Create(new ToolCallId("call_1"), new ContentBlock[] { new TextBlock("done") }),
        };
        var tools = new[]
        {
            new ToolSchema("get_weather", "Current weather", JsonDocument.Parse("{\"type\":\"object\"}").RootElement),
        };

        Drain(adapter, Request(messages, system: "You are helpful", tools: tools, temperature: 0.7, maxTokens: 512));

        var http = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, http.Method);
        Assert.Equal("https://api.deepseek.com/chat/completions", http.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test-123", http.Headers.Authorization?.ToString());
        Assert.True(http.Headers.Accept.Any(entry => entry.MediaType == "text/event-stream"), "accept must request the event stream");
        Assert.Equal("application/json", http.Content!.Headers.ContentType?.MediaType);

        var expected = JsonNode.Parse("""
        {
          "model": "deepseek-chat",
          "messages": [
            { "role": "system", "content": "You are helpful" },
            { "role": "user", "content": "Hello" },
            { "role": "assistant", "content": "Hi!", "reasoning_content": "thinking...",
              "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "todo_write", "arguments": "{\"todos\":[]}" } } ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "done" }
          ],
          "stream": true,
          "stream_options": { "include_usage": true },
          "tools": [ { "type": "function", "function": { "name": "get_weather", "description": "Current weather", "parameters": { "type": "object" } } } ],
          "temperature": 0.7,
          "max_tokens": 512
        }
        """);
        var actual = JsonNode.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(JsonNode.DeepEquals(expected, actual), "request body must match the pinned wire JSON");
    }

    public static void OptionalFields_AreOmitted_WhenAbsent()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        Drain(adapter, Request(messages: new[] { Messages.CreateUserMessage(new ContentBlock[] { new TextBlock("hi") }) }));
        var body = Body(handler).AsObject();
        Assert.Null(body["tools"]);
        Assert.Null(body["temperature"]);
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["thinking"]);
        Assert.Null(body["reasoning_effort"]);
        Assert.Equal(true, (bool)body["stream"]!);
        Assert.Equal(true, (bool)body["stream_options"]!["include_usage"]!);
    }

    public static void ThinkingDisabled_FromConfig()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test", Thinking: false), handler);
        Drain(adapter, Request());
        var body = Body(handler).AsObject();
        Assert.Equal("disabled", (string)body["thinking"]!["type"]!);
        Assert.Null(body["reasoning_effort"]);
    }

    public static void ThinkingEnabled_WithEffort_FromConfig()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test", Thinking: true, ReasoningEffort: DeepSeekReasoningEffort.High), handler);
        Drain(adapter, Request());
        var body = Body(handler).AsObject();
        Assert.Equal("enabled", (string)body["thinking"]!["type"]!);
        Assert.Equal("high", (string)body["reasoning_effort"]!);
    }

    public static void EffortOff_DisablesThinking_WithoutEffortField()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test", ReasoningEffort: DeepSeekReasoningEffort.Off), handler);
        Drain(adapter, Request());
        var body = Body(handler).AsObject();
        Assert.Equal("disabled", (string)body["thinking"]!["type"]!);
        Assert.Null(body["reasoning_effort"]);
    }

    public static void ThinkingDisabledWithEffort_ThrowsAtConstruction()
    {
        var error = Assert.Throws<LlmError>(() => new DeepSeekAdapter(
            new DeepSeekConfig(ApiKey: "sk-test", Thinking: false, ReasoningEffort: DeepSeekReasoningEffort.High)));
        Assert.Equal("UNSUPPORTED_REASONING_EFFORT", error.Code);
    }

    public static void ConfigApiKey_WinsOverEnvironment()
    {
        SetEnv(DeepSeekAdapter.ApiKeyEnvVar, "sk-env");
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-config"), handler);
        Drain(adapter, Request());
        Assert.Equal("Bearer sk-config", Assert.Single(handler.Requests).Headers.Authorization?.ToString());
    }

    public static void EnvironmentApiKey_Fallback()
    {
        SetEnv(DeepSeekAdapter.ApiKeyEnvVar, "sk-env");
        try
        {
            var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
            var adapter = new DeepSeekAdapter(new DeepSeekConfig(), handler);
            Drain(adapter, Request());
            Assert.Equal("Bearer sk-env", Assert.Single(handler.Requests).Headers.Authorization?.ToString());
        }
        finally
        {
            SetEnv(DeepSeekAdapter.ApiKeyEnvVar, null);
        }
    }

    public static void MissingApiKey_ThrowsMissingCredential()
    {
        SetEnv(DeepSeekAdapter.ApiKeyEnvVar, null);
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("MISSING_CREDENTIAL", error.Code);
        Assert.Null(error.Failure.Status);
    }

    public static void ConfigBaseUrl_WinsOverEnvironment()
    {
        SetEnv(DeepSeekAdapter.BaseUrlEnvVar, "https://env.example.com");
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test", BaseUrl: "https://cfg.example.com/v1/"), handler);
        Drain(adapter, Request());
        Assert.Equal("https://cfg.example.com/v1/chat/completions", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    public static void EnvironmentBaseUrl_Fallback()
    {
        SetEnv(DeepSeekAdapter.BaseUrlEnvVar, "https://env.example.com");
        try
        {
            var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
            var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
            Drain(adapter, Request());
            Assert.Equal("https://env.example.com/chat/completions", Assert.Single(handler.Requests).RequestUri!.ToString());
        }
        finally
        {
            SetEnv(DeepSeekAdapter.BaseUrlEnvVar, null);
        }
    }

    public static void DefaultBaseUrl_IsPublicApi()
    {
        SetEnv(DeepSeekAdapter.BaseUrlEnvVar, null);
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        Drain(adapter, Request());
        Assert.Equal($"{DeepSeekAdapter.PublicBaseUrl}/chat/completions", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    public static void Error401_MapsToAuth_WithProviderMessage()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.Unauthorized,
                """{"error":{"message":"Invalid API key","type":"authentication_error","code":"invalid_api_key"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("AUTH", error.Code);
        Assert.Equal("Invalid API key", error.Message);
        Assert.Equal(401, error.Failure.Status);
    }

    public static void Error429_MapsToRateLimit()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.TooManyRequests,
                """{"error":{"message":"Rate limit reached","type":"rate_limit_error"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("RATE_LIMIT", error.Code);
        Assert.Equal(429, error.Failure.Status);
    }

    public static void Error429WithQuotaWording_MapsToQuota()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.TooManyRequests,
                """{"error":{"message":"Insufficient Quota","type":"insufficient_quota"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("QUOTA", error.Code);
    }

    public static void Error500_MapsToServer()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.InternalServerError,
                """{"error":{"message":"upstream blew up","type":"server_error"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("SERVER", error.Code);
        Assert.Equal(500, error.Failure.Status);
    }

    public static void Error400ContextWindow_MapsToContextWindowExceeded()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.BadRequest,
                """{"error":{"message":"This model's maximum context length is 128000 tokens. However, your messages resulted in 128100 tokens."}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("CONTEXT_WINDOW_EXCEEDED", error.Code);
    }

    public static void Error400Generic_MapsToInvalidRequest()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.BadRequest,
                """{"error":{"message":"bad field","type":"invalid_request_error"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("INVALID_REQUEST", error.Code);
    }

    public static void Error413_MapsToInvalidRequest()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.RequestEntityTooLarge,
                """{"error":{"message":"request too large"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("INVALID_REQUEST", error.Code);
        Assert.Equal(413, error.Failure.Status);
    }

    public static void Error418_MapsToHttp418()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error((HttpStatusCode)418, """{"error":{"message":"teapot"}}""")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("HTTP_418", error.Code);
    }

    public static void MalformedErrorBody_KeepsStatusMessage()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Error(HttpStatusCode.InternalServerError, "<html>gateway exploded</html>")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("SERVER", error.Code);
        Assert.Equal("DeepSeek API error (HTTP 500)", error.Message);
    }

    public static void CancelledBeforeRequest_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpMessageHandler { Responder = (_, _) => Task.FromResult(Responses.Sse("[DONE]")) };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var enumerator = adapter.StreamAsync(Request(), cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
    }

    public static void CancelledMidStream_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowSseStream("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n")),
            }),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        using var cts = new CancellationTokenSource();
        var enumerator = adapter.StreamAsync(Request(), cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "first chunk must arrive");
        Assert.True(enumerator.Current is BlockStart { BlockType: "text" }, "first chunk must open the text block");
        cts.Cancel();
        // Buffered deltas of the already-fetched payload may still drain; the blocked read is
        // what surfaces the cancellation.
        var caught = false;
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) { }
        }
        catch (OperationCanceledException)
        {
            caught = true;
        }
        Assert.True(caught, "mid-stream cancellation must surface as OperationCanceledException");
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public static void TransportFailure_MapsToTransport()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => throw new HttpRequestException("connection refused"),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var error = CaptureAdapterError(adapter, Request());
        Assert.Equal("TRANSPORT", error.Code);
        Assert.Null(error.Failure.Status);
    }

    public static void FullStream_AssemblesIntoMessage()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(Responses.Sse(
                """{"choices":[{"delta":{"content":"Hello "}}]}""",
                """{"choices":[{"delta":{"content":"world"}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "[DONE]")),
        };
        var adapter = new DeepSeekAdapter(new DeepSeekConfig(ApiKey: "sk-test"), handler);
        var assembler = new BlockAssembler();
        var enumerator = adapter.StreamAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        while (true)
        {
            if (!enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) break;
            assembler.Push(enumerator.Current);
        }
        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks()));
        Assert.Equal("Hello world", block.Text);
        Assert.True(assembler.Finish is Stop, "finish must be stop");
    }
}
