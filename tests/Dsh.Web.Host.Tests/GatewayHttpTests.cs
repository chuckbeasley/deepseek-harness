using System.Net;
using System.Net.Sockets;
using System.Text;
using Cordis.Core;
using Dsh.Web.Host;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The unary HTTP carrier: envelope round-trips, the exact status vocabulary (404/415/400/413),
/// the result-error branch (200 with coded failures), and the rpcId echo.
/// </summary>
public static class GatewayHttpTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Spine : IDisposable
    {
        public required Context Ctx { get; init; }

        public required DshRpcRegistry Rpc { get; init; }

        public required Dsh.Web.Host.WebHostService Host { get; init; }

        public static Spine Create()
        {
            var ctx = new Context();
            var rpc = new DshRpcRegistry(ctx);
            var host = new Dsh.Web.Host.WebHostService(ctx, new Dsh.Web.Host.WebHostConfig(Port: FreePort()));
            host.StartAsync().GetAwaiter().GetResult();
            return new Spine { Ctx = ctx, Rpc = rpc, Host = host };
        }

        public void Dispose()
        {
            Host.StopAsync().GetAwaiter().GetResult();
            Ctx.Dispose();
        }
    }

    private static string Post(Spine spine, string endpoint, string body, string contentType = "application/json")
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, spine.Host.ListenUrl + "/api/" + endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        return response.StatusCode + ":" + response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    public static void UnaryRoundTrip_EchoesRpcIdAndValue()
    {
        using var spine = Spine.Create();
        using var registration = spine.Rpc.Register(new Dsh.Web.Host.RpcMethod("echo/hello", (args, _) =>
            Task.FromResult<System.Text.Json.JsonElement?>(args)));
        var raw = Post(spine, "echo/hello",
            "{\"type\":\"client-request\",\"rpcId\":\"rpc-1\",\"method\":\"echo/hello\",\"payload\":{\"args\":{\"name\":\"world\"}}}");
        using var document = System.Text.Json.JsonDocument.Parse(raw.Split(':', 2)[1]);
        var root = document.RootElement;
        Assert.True(root.GetProperty("type").GetString() == "server-response", "the server envelope type");
        Assert.True(root.GetProperty("rpcId").GetString() == "rpc-1", "the rpcId echoes verbatim");
        Assert.True(root.GetProperty("result").GetProperty("ok").GetBoolean(), "the call succeeds");
        Assert.True(root.GetProperty("result").GetProperty("value").GetProperty("name").GetString() == "world", "the value round-trips");
    }

    public static void UnknownEndpoint_SettlesInvocationUnavailable_WithHttp200()
    {
        using var spine = Spine.Create();
        var raw = Post(spine, "no/such", "{\"type\":\"client-request\",\"rpcId\":\"rpc-2\",\"method\":\"no/such\",\"payload\":{\"args\":{}}}");
        Assert.True(raw.StartsWith("OK:", StringComparison.OrdinalIgnoreCase), "business failures ride HTTP 200");
        using var document = System.Text.Json.JsonDocument.Parse(raw.Split(':', 2)[1]);
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean(), "the call fails");
        Assert.True(result.GetProperty("error").GetProperty("code").GetString() == "gateway/invocation-unavailable", "the coded failure");
    }

    public static void InvalidEnvelope_SettlesBadRequest_WithFallbackRpcId()
    {
        using var spine = Spine.Create();
        var raw = Post(spine, "echo/hello", "{\"type\":\"wrong\",\"method\":\"echo/hello\",\"payload\":{\"args\":{}}}");
        using var document = System.Text.Json.JsonDocument.Parse(raw.Split(':', 2)[1]);
        var root = document.RootElement;
        Assert.True(root.GetProperty("rpcId").GetString() == "invalid-request", "the fallback rpc id");
        var error = root.GetProperty("result").GetProperty("error");
        Assert.True(error.GetProperty("code").GetString() == "gateway/bad-request", "the bad-request code");
        Assert.True(error.GetProperty("message").GetString().Contains("invalid client-request", StringComparison.Ordinal), "the message");
    }

    public static void MethodMismatch_SettlesBadRequest()
    {
        using var spine = Spine.Create();
        var raw = Post(spine, "echo/hello", "{\"type\":\"client-request\",\"rpcId\":\"rpc-3\",\"method\":\"other/method\",\"payload\":{\"args\":{}}}");
        using var document = System.Text.Json.JsonDocument.Parse(raw.Split(':', 2)[1]);
        var error = document.RootElement.GetProperty("result").GetProperty("error");
        Assert.True(error.GetProperty("code").GetString() == "gateway/bad-request", "the bad-request code");
        Assert.True(error.GetProperty("message").GetString().Contains("does not match endpoint", StringComparison.Ordinal), "the mismatch message");
    }

    public static void NonJsonContentType_Answers415()
    {
        using var spine = Spine.Create();
        var raw = Post(spine, "echo/hello", "{}", "text/plain");
        Assert.True(raw.StartsWith("UnsupportedMediaType:", StringComparison.OrdinalIgnoreCase), "415 for non-JSON content type");
    }

    public static void NonPostMethod_Answers404()
    {
        using var spine = Spine.Create();
        using var client = new HttpClient();
        var response = client.GetAsync(spine.Host.ListenUrl + "/api/echo/hello").GetAwaiter().GetResult();
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, "GET on an /api endpoint is 404");
    }

    public static void InvalidSegment_Answers404()
    {
        using var spine = Spine.Create();
        var raw = Post(spine, "../secret/leak", "{\"type\":\"client-request\",\"rpcId\":\"r\",\"method\":\"x/y\",\"payload\":{\"args\":{}}}");
        Assert.True(raw.StartsWith("NotFound:", StringComparison.OrdinalIgnoreCase), "dot-dot segments are rejected at the route");
    }
}
