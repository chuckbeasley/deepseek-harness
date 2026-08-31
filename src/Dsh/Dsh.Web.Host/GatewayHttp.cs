using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dsh.Web.Host;

/// <summary>
/// The unary HTTP carrier (port of the TS rpc-host): <c>POST /api/&lt;namespace&gt;/&lt;method&gt;</c>
/// with the client-request/server-response envelopes. HTTP status carries only carrier-level
/// failures (404 route, 415 content type, 400 body, 413 body cap, 500 handler bug); business
/// failures ride the result-error branch with HTTP 200.
/// </summary>
public static class GatewayHttp
{
    /// <summary>The API path prefix owned by the RPC connection.</summary>
    public const string ApiPath = "/api";

    /// <summary>The body ceiling for unary calls.</summary>
    public const long MaxRequestBodyBytes = 300 * 1024 * 1024;

    private static readonly System.Text.RegularExpressions.Regex SegmentPattern =
        new("^[A-Za-z0-9_$.-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Map the unary carrier onto the application.</summary>
    public static void MapGateway(this WebApplication app, DshRpcRegistry registry)
    {
        // One catch-all route with an explicit method gate: any non-POST method on an /api
        // endpoint answers 404 (the TS host semantics), not the framework's 405.
        app.Map($"{ApiPath}/{{ns}}/{{method}}", async (HttpContext http, string ns, string method) =>
        {
            if (!HttpMethods.IsPost(http.Request.Method))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await HandleAsync(http, registry, ns, method);
        });
    }

    private static async Task HandleAsync(HttpContext http, DshRpcRegistry registry, string ns, string method)
    {
        var endpoint = $"{ns}/{method}";
        if (!IsValidSegment(ns) || !IsValidSegment(method))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!IsJsonContentType(http.Request.ContentType))
        {
            http.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        JsonElement body;
        try
        {
            body = await ReadBodyAsync(http.Request.Body, http.Request.ContentLength, http.RequestAborted);
        }
        catch (BodyTooLargeException)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            http.Response.Headers.Connection = "close";
            return;
        }
        catch (JsonException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var (request, envelopeError) = ClientRequestEnvelope.Parse(body);
        if (envelopeError is not null)
        {
            await WriteResponseAsync(http, new ServerResponse(
                RpcIdOf(body, "invalid-request"), null, envelopeError));
            return;
        }
        if (request!.Method != endpoint)
        {
            await WriteResponseAsync(http, new ServerResponse(
                request.RpcId, null,
                new RpcError(RpcErrorCodes.BadRequest, $"method {request.Method} does not match endpoint {endpoint}",
                    JsonSerializer.SerializeToElement(new { issues = Array.Empty<object>() }))));
            return;
        }
        if (!ClientRequestEnvelope.IsRemotePayload(request.Payload))
        {
            await WriteResponseAsync(http, new ServerResponse(
                request.RpcId, null,
                new RpcError(RpcErrorCodes.ArgumentsInvalid, "remote payload must be exactly { args: { ... } }")));
            return;
        }
        var args = request.Payload.GetProperty("args").Clone();
        var response = await registry.InvokeAsync(new RpcRequest(endpoint, args), http.RequestAborted);
        await WriteResponseAsync(http, new ServerResponse(request.RpcId, response.Result, response.Error));
    }

    /// <summary>Write the exact server envelope (the value slot is omitted when absent).</summary>
    private static async Task WriteResponseAsync(HttpContext http, ServerResponse response)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "server-response");
            writer.WriteString("rpcId", response.RpcId);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            if (response.Ok)
            {
                writer.WriteBoolean("ok", true);
                if (response.Value is JsonElement value)
                {
                    writer.WritePropertyName("value");
                    value.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteBoolean("ok", false);
                writer.WritePropertyName("error");
                RpcErrorCodec.Encode(response.Error!).WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        http.Response.ContentLength = stream.Length;
        stream.Position = 0;
        await stream.CopyToAsync(http.Response.Body, http.RequestAborted);
    }

    private static string RpcIdOf(JsonElement body, string fallback)
        => body.TryGetProperty("rpcId", out var rpcId) && rpcId.ValueKind == JsonValueKind.String
            ? rpcId.GetString()!
            : fallback;

    private static bool IsValidSegment(string segment)
        => segment.Length > 0 && segment != "." && segment != ".." && SegmentPattern.IsMatch(segment);

    private static bool IsJsonContentType(string? value)
    {
        if (value is null) return false;
        var mediaType = value.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> ReadBodyAsync(Stream body, long? contentLength, CancellationToken cancellationToken)
    {
        if (contentLength is long declared && declared > MaxRequestBodyBytes)
        {
            throw new BodyTooLargeException();
        }
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0L;
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaxRequestBodyBytes) throw new BodyTooLargeException();
            memory.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(memory.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class BodyTooLargeException : Exception;
}
