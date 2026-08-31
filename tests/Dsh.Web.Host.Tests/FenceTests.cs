using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cordis.Core;
using Dsh.Credentials;
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
            // Tamper the signature's FIRST character: it contributes a full 6 bits to the decoded
            // bytes. The last character of a 43-char base64url signature contributes only 4 bits
            // (its low 2 bits are discarded), so an 'A'<->'B' flip there decodes to identical
            // bytes and would not break the signature.
            var sigStart = cookie.LastIndexOf('.') + 1;
            var tampered = cookie[..sigStart] + (cookie[sigStart] == 'A' ? 'B' : 'A') + cookie[(sigStart + 1)..];
            var response = PostEnvelope(client, "/api/session/list", "r3", tampered);
            Assert.True((int)response.StatusCode == 401,
                $"a tampered cookie is refused (got {(int)response.StatusCode}: {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()})");
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    public static void PersistentSecret_CookiesSurviveRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-fence-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var managedPath = Path.Combine(root, ".credentials.env");
        var port = FreePort();
        string cookie;
        var firstCtx = new Context();
        var firstHost = BootWithCredentials(firstCtx, managedPath, port);
        try
        {
            cookie = MintCookie(firstHost.Client, firstHost.Host.Fence!.LaunchToken);
        }
        finally
        {
            firstHost.Client.Dispose();
            firstHost.Host.StopAsync().GetAwaiter().GetResult();
            firstCtx.Dispose();
        }

        var secondCtx = new Context();
        var secondHost = BootWithCredentials(secondCtx, managedPath, port);
        try
        {
            var response = PostEnvelope(secondHost.Client, "/api/session/list", "r4", cookie);
            Assert.True((int)response.StatusCode == 200,
                "the pre-restart cookie still authenticates: the signing secret survives through the credentials store");
        }
        finally
        {
            secondHost.Client.Dispose();
            secondHost.Host.StopAsync().GetAwaiter().GetResult();
            secondCtx.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Boot a fenced host whose signing secret resolves through a credentials provider over one managed file.</summary>
    private static (Context Ctx, WebHostService Host, HttpClient Client) BootWithCredentials(Context ctx, string managedPath, int port)
    {
        _ = new LocalCredentialsProvider(ctx, new LocalCredentialsConfig
        {
            ManagedPath = managedPath,
            ProjectEnvPath = null,
            UserEnvPath = null,
        }, _ => null);
        var registry = new DshRpcRegistry(ctx);
        _ = registry.Register(new RpcMethod("session/list", (_, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }))));
        var host = new WebHostService(ctx, new WebHostConfig(Port: port), map: app => app.MapGet("/", () => Results.Text("index")));
        host.StartAsync().GetAwaiter().GetResult();
        return (ctx, host, new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(host.ListenUrl!) });
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
        // The tests manage cookies explicitly; a container would add a second, hidden cookie
        // source to every request.
        return (ctx, host, new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(origin) });
    }
}
