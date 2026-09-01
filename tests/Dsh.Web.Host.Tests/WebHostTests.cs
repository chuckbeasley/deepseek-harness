using System.Net;
using System.Net.Sockets;
using System.Text;
using Harness.Cordis.Core;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The Kestrel host: boot on a free loopback port, HTTP GET round-trip, and the SignalR hub
/// invoke channel over a real connection.
/// </summary>
public static class WebHostTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void BootsAndServesTheMappedEndpoints()
    {
        var ctx = new Context();
        var rpc = new DshRpcRegistry(ctx);
        using var host = new WebHostService(ctx, new WebHostConfig(Port: FreePort(), AuthFence: false), map: app =>
        {
            app.MapGet("/", () => "dsh web");
            app.MapGet("/health", () => "ok");
        });
        host.StartAsync().GetAwaiter().GetResult();
        Assert.NotNull(host.ListenUrl, "the host binds an address");
        using var client = new HttpClient();
        var root = client.GetStringAsync(host.ListenUrl + "/").GetAwaiter().GetResult();
        Assert.True(root == "dsh web", $"GET / serves the app shell, got \"{root}\"");
        var health = client.GetStringAsync(host.ListenUrl + "/health").GetAwaiter().GetResult();
        Assert.True(health == "ok", "GET /health answers");
        host.StopAsync().GetAwaiter().GetResult();
        ctx.Dispose();
    }

    public static void HubInvoke_RoundTripsThroughTheRegistry()
    {
        var ctx = new Context();
        var rpc = new DshRpcRegistry(ctx);
        using var registration = rpc.Register(new RpcMethod("echo/hello", (args, _) =>
            Task.FromResult<JsonElement?>(args)));
        using var host = new WebHostService(ctx, new WebHostConfig(Port: FreePort(), AuthFence: false));
        host.StartAsync().GetAwaiter().GetResult();
        Assert.NotNull(host.ListenUrl, "the host binds an address");
        try
        {
            var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
                .WithUrl(host.ListenUrl + "/hub")
                .Build();
            connection.StartAsync().GetAwaiter().GetResult();
            try
            {
                var response = connection.InvokeAsync<RpcResponse>(
                    "Invoke", "echo/hello", JsonSerializer.SerializeToElement(new { name = "world" })).GetAwaiter().GetResult();
                Assert.True(response.Ok, "the hub call succeeds");
                Assert.Equal("world", response.Result!.Value.GetProperty("name").GetString(), "the args round-trip through the hub");
            }
            finally
            {
                connection.DisposeAsync().GetAwaiter().GetResult();
            }
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void HubInvoke_UnknownEndpoint_SettlesMethodNotFound()
    {
        var ctx = new Context();
        var rpc = new DshRpcRegistry(ctx);
        using var host = new WebHostService(ctx, new WebHostConfig(Port: FreePort(), AuthFence: false));
        host.StartAsync().GetAwaiter().GetResult();
        try
        {
            var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
                .WithUrl(host.ListenUrl + "/hub")
                .Build();
            connection.StartAsync().GetAwaiter().GetResult();
            try
            {
                var response = connection.InvokeAsync<RpcResponse>(
                    "Invoke", "no/such-method", (JsonElement?)null).GetAwaiter().GetResult();
                Assert.False(response.Ok, "the hub call fails");
                Assert.Equal("gateway/invocation-unavailable", response.Error!.Code, "the coded failure travels the wire");
            }
            finally
            {
                connection.DisposeAsync().GetAwaiter().GetResult();
            }
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    public static void Stop_ClosesTheListener()
    {
        var ctx = new Context();
        using var host = new WebHostService(ctx, new WebHostConfig(Port: FreePort(), AuthFence: false));
        host.StartAsync().GetAwaiter().GetResult();
        var url = host.ListenUrl!;
        host.StopAsync().GetAwaiter().GetResult();
        var failed = false;
        try
        {
            using var client = new HttpClient();
            _ = client.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            failed = true;
        }
        Assert.True(failed, "a request after stop is refused at the socket");
        ctx.Dispose();
    }
}


