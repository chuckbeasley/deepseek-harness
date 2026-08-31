using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cordis.Core;
using Dsh.Web.Host;
using Microsoft.AspNetCore.Http;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The loopback process-token fence: the trust fence (403), the browser-session cookie (401), the
/// launch-token exchange that mints it, and the gated API surfaces (gateway, hub, mux) — all over
/// a real Kestrel host with the fence enabled.
/// </summary>
public static class FenceTests
{
    public static void Index_WithoutToken_Settles401()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var response = client.GetAsync("/").GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 401, "the index is gated");
            Assert.True(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                .Contains("dsh web authentication required", StringComparison.Ordinal), "the 401 names the URL flow");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TokenExchange_MintsCookie_ThenIndexServes()
    {
        var (ctx, host, client) = Boot(out var origin);
        try
        {
            var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(origin) };
            var exchange = noRedirect.GetAsync($"/?token={host.Fence!.LaunchToken}").GetAwaiter().GetResult();
            Assert.True((int)exchange.StatusCode == 303, "a valid launch token redirects");
            Assert.Equal("/", exchange.Headers.Location?.ToString());
            Assert.True(exchange.Headers.TryGetValues("Set-Cookie", out var cookies), "the exchange mints the cookie");
            var cookie = cookies!.First().Split(';')[0];

            var without = noRedirect.GetAsync("/").GetAwaiter().GetResult();
            Assert.True((int)without.StatusCode == 401, "without the cookie the index still refuses");

            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var served = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)served.StatusCode == 200, "the cookie serves the index");
            Assert.Equal("index", served.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void Index_WithWrongToken_Settles401()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var response = client.GetAsync("/?token=wrong-token").GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 401, "a wrong launch token is refused");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void Api_WithoutCookie_Settles401()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var response = PostEnvelope(client, "/api/session/list", "r1");
            Assert.True((int)response.StatusCode == 401, "the gateway is gated");
            Assert.Equal("unauthorized", response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void Api_WithCookie_RoundTrips()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var cookie = MintCookie(client, host.Fence!.LaunchToken);
            var response = PostEnvelope(client, "/api/session/list", "r2", cookie);
            Assert.True((int)response.StatusCode == 200, "the cookie opens the gateway");
            var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
            Assert.Equal("server-response", body.GetProperty("type").GetString());
            Assert.Equal("r2", body.GetProperty("rpcId").GetString());
            Assert.True(body.GetProperty("result").GetProperty("ok").GetBoolean(), "the gated call round-trips");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void UntrustedHost_Settles403()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            request.Headers.Host = "evil.example";
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 403, "a rebound Host is refused");
            Assert.Equal("forbidden", response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void CrossSiteOrigin_Settles403()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            foreach (var origin in new[] { "http://evil.example", "null" })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
                request.Headers.TryAddWithoutValidation("Origin", origin);
                var response = client.SendAsync(request).GetAwaiter().GetResult();
                Assert.True((int)response.StatusCode == 403, $"origin {origin} is refused");
            }
            var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            var metadata = client.SendAsync(crossSite).GetAwaiter().GetResult();
            Assert.True((int)metadata.StatusCode == 403, "an explicit cross-site marker is refused");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TamperedCookie_Settles401()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var cookie = MintCookie(client, host.Fence!.LaunchToken);
            var last = cookie.Length - 1;
            var tampered = cookie[..last] + (cookie[last] == 'A' ? 'B' : 'A');
            var response = PostEnvelope(client, "/api/session/list", "r3", tampered);
            Assert.True((int)response.StatusCode == 401, "a tampered cookie is refused");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void HubNegotiate_WithoutCookie_Settles401()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var response = client.PostAsync("/hub/negotiate?negotiateVersion=1", new StringContent("", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 401, "the hub is gated");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void MuxPath_WithoutCookie_Settles401_WithCookieReachesHandler()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var gated = client.GetAsync("/api/remote.mux").GetAwaiter().GetResult();
            Assert.True((int)gated.StatusCode == 401, "the mux upgrade is gated with plain HTTP 401");

            var cookie = MintCookie(client, host.Fence!.LaunchToken);
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/remote.mux");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var open = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)open.StatusCode == 400, "with the cookie the request reaches the mux handler (no WS upgrade headers)");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void Cookie_IsBoundToItsAuthority()
    {
        var (ctx, host, client) = Boot(out var origin);
        try
        {
            var cookie = MintCookie(client, host.Fence!.LaunchToken);
            var port = new Uri(origin).Port;
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            request.Headers.Host = $"localhost:{port}";
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 401, "a cookie minted for 127.0.0.1 does not authenticate localhost");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    /// <summary>Run the launch-token exchange and return the minted cookie value.</summary>
    private static string MintCookie(HttpClient client, string launchToken)
    {
        var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress };
        var exchange = noRedirect.GetAsync($"/?token={launchToken}").GetAwaiter().GetResult();
        Assert.True((int)exchange.StatusCode == 303, "the launch token must mint the cookie");
        Assert.True(exchange.Headers.TryGetValues("Set-Cookie", out var cookies), "the exchange must set the cookie");
        return cookies!.First().Split(';')[0];
    }

    private static HttpResponseMessage PostEnvelope(HttpClient client, string path, string rpcId, string? cookie = null)
    {
        var method = path.StartsWith("/api/", StringComparison.Ordinal) ? path["/api/".Length..] : path.TrimStart('/');
        var body = JsonSerializer.Serialize(new
        {
            type = "client-request",
            rpcId,
            method,
            payload = new { args = new { } },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    private static (Context Ctx, WebHostService Host, HttpClient Client) Boot(out string origin)
    {
        var ctx = new Context();
        var registry = new DshRpcRegistry(ctx);
        _ = registry.Register(new RpcMethod("session/list", (_, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }))));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new WebHostService(ctx, new WebHostConfig(Port: port), map: app => app.MapGet("/", () => Results.Text("index")));
        host.StartAsync().GetAwaiter().GetResult();
        origin = host.ListenUrl!;
        return (ctx, host, new HttpClient { BaseAddress = new Uri(origin) });
    }
}
