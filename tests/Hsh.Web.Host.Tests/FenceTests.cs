using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Credentials;
using Harness.Web.Host;
using Microsoft.AspNetCore.Http;

namespace Harness.Web.Host.Tests;

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
                .Contains("hsh web authentication required", StringComparison.Ordinal), "the 401 names the URL flow");
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

    public static void TrustedHost_MatchesDeclaredAuthority_UntrustedStill403()
    {
        var (ctx, host, client) = BootTrusted(new[] { "harness.example" }, out _);
        try
        {
            var trusted = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            trusted.Headers.Host = "harness.example";
            var passed = client.SendAsync(trusted).GetAwaiter().GetResult();
            Assert.True((int)passed.StatusCode == 401,
                "a declared trusted Host passes the trust fence and is refused only by auth (got " + (int)passed.StatusCode + ")");

            var untrusted = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            untrusted.Headers.Host = "evil.example";
            var refused = client.SendAsync(untrusted).GetAwaiter().GetResult();
            Assert.True((int)refused.StatusCode == 403, "an undeclared Host is still refused");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TrustedHost_PortlessEntry_MatchesAnyPort()
    {
        var (ctx, host, client) = BootTrusted(new[] { "harness.example" }, out _);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            request.Headers.Host = "harness.example:9999";
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 401,
                "a port-less entry trusts the hostname on any port (got " + (int)response.StatusCode + ")");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TrustedHost_ExplicitPortEntry_MatchesOnlyThatPort()
    {
        var (ctx, host, client) = BootTrusted(new[] { "harness.example:3080" }, out _);
        try
        {
            var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            wrong.Headers.Host = "harness.example:9999";
            var refused = client.SendAsync(wrong).GetAwaiter().GetResult();
            Assert.True((int)refused.StatusCode == 403, "an explicit port entry does not broaden to other ports");

            var exact = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            exact.Headers.Host = "harness.example:3080";
            var passed = client.SendAsync(exact).GetAwaiter().GetResult();
            Assert.True((int)passed.StatusCode == 401,
                "the exact authority passes the trust fence (got " + (int)passed.StatusCode + ")");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TrustedHost_ExplicitDefaultPort_MatchesDefaultPortRequests()
    {
        var (ctx, host, client) = BootTrusted(new[] { "harness.example:80" }, out _);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/list");
            request.Headers.Host = "harness.example";
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 401,
                "the http default port is dropped on both sides, so an explicit :80 entry equals the port-less authority (got " + (int)response.StatusCode + ")");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TrustedHost_CookieRoundTrip_UnderTheDeclaredAuthority()
    {
        var (ctx, host, client) = BootTrusted(new[] { "harness.example" }, out _);
        try
        {
            var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(host.ListenUrl!) };
            var exchange = new HttpRequestMessage(HttpMethod.Get, $"/?token={host.Fence!.LaunchToken}");
            exchange.Headers.Host = "harness.example";
            var minted = noRedirect.SendAsync(exchange).GetAwaiter().GetResult();
            Assert.True((int)minted.StatusCode == 303,
                $"the exchange mints under the trusted authority (got {(int)minted.StatusCode}: {minted.Content.ReadAsStringAsync().GetAwaiter().GetResult()})");
            Assert.True(minted.Headers.TryGetValues("Set-Cookie", out var cookies), "the exchange sets the cookie");
            var cookie = cookies!.First().Split(';')[0];

            var body = JsonSerializer.Serialize(new
            {
                type = "client-request",
                rpcId = "r5",
                method = "session/list",
                payload = new { args = new { } },
            });
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/list")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Host = "harness.example";
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            Assert.True((int)response.StatusCode == 200, "the trusted authority round-trips with its own cookie");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void TrustedHosts_MalformedEntry_FailsLoud()
    {
        foreach (var entry in new[]
        {
            "harness.internal/path",
            "user@harness.internal",
            "harness.internal: ",
            "harness.internal:99999",
            "harness.internal:080",
            "127.1",
            "010.0.0.1",
            "256.1.1.1",
            "",
        })
        {
            Assert.Throws<ArgumentException>(
                () => WebAuthFence.AssertTrustedAuthority(entry),
                $"entry \"{entry}\" must be refused as not a bare authority");
        }
        foreach (var entry in new[]
        {
            "harness.example",
            "HARNESS.example",
            "harness.example:3080",
            "harness.example:80",
            "127.0.0.1:3080",
            "[::1]:3080",
            "192.168.1.5",
        })
        {
            WebAuthFence.AssertTrustedAuthority(entry);
        }
    }

    public static void LanTrust_IsDerivedForTheAllInterfacesBind()
    {
        var derived = WebHostService.ResolveLanTrust("0.0.0.0", new[] { "harness.example" });
        Assert.True(derived.Count >= 1, "the all-interfaces bind derives the machine's LAN literals");
        Assert.Equal("harness.example", derived[^1], "the configured entries follow the derived ones");
        foreach (var entry in derived.Take(derived.Count - 1))
        {
            Assert.True(System.Net.IPAddress.TryParse(entry, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
                $"derived entry \"{entry}\" must be an IPv4 literal");
            Assert.True(!System.Net.IPAddress.IsLoopback(address), $"derived entry \"{entry}\" must not be loopback");
        }
        var loopback = WebHostService.ResolveLanTrust("127.0.0.1", new[] { "harness.example" });
        Assert.Equal(1, loopback.Count, "a loopback bind derives nothing");
        Assert.Equal("harness.example", loopback[0], "the configured entry passes through");
    }

    private static (Context Ctx, WebHostService Host, HttpClient Client) BootTrusted(IReadOnlyList<string> trustedHosts, out string origin)
    {
        var ctx = new Context();
        var registry = new HshRpcRegistry(ctx);
        _ = registry.Register(new RpcMethod("session/list", (_, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }))));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new WebHostService(ctx, new WebHostConfig(Port: port, TrustedHosts: trustedHosts), map: app => app.MapGet("/", () => Results.Text("index")));
        host.StartAsync().GetAwaiter().GetResult();
        origin = host.ListenUrl!;
        return (ctx, host, new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(origin) });
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
        var root = Path.Combine(Path.GetTempPath(), "hsh-fence-secret-" + Guid.NewGuid().ToString("N"));
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
        var registry = new HshRpcRegistry(ctx);
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
        var registry = new HshRpcRegistry(ctx);
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
