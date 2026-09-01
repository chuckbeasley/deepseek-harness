using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The RPC registry: registration, dispatch, the coded failure vocabulary, and the effect-owned
/// teardown.
/// </summary>
public static class RpcRegistryTests
{
    private sealed class Spine : IDisposable
    {
        public required Context Ctx { get; init; }

        public required DshRpcRegistry Rpc { get; init; }

        public static Spine Create()
        {
            var ctx = new Context();
            var rpc = new DshRpcRegistry(ctx);
            return new Spine { Ctx = ctx, Rpc = rpc };
        }

        public void Dispose() => Ctx.Dispose();
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public static void Register_AndDispatch_ReturnsTheResult()
    {
        using var spine = Spine.Create();
        using var registration = spine.Rpc.Register(new RpcMethod("echo/ping", (args, _) =>
            Task.FromResult<JsonElement?>(args)));
        var response = spine.Rpc.InvokeAsync(new RpcRequest("echo/ping", Json(new { hello = "world" }))).GetAwaiter().GetResult();
        Assert.True(response.Ok, "the call succeeds");
        Assert.NotNull(response.Result, "the result is present");
        Assert.Equal("world", response.Result!.Value.GetProperty("hello").GetString());
    }

    public static void UnknownEndpoint_SettlesMethodNotFound()
    {
        using var spine = Spine.Create();
        var response = spine.Rpc.InvokeAsync(new RpcRequest("no/such-method", null)).GetAwaiter().GetResult();
        Assert.False(response.Ok, "the call fails");
        Assert.Equal("gateway/invocation-unavailable", response.Error!.Code);
        Assert.True(response.Error.Message.Contains("no/such-method"), "the endpoint is named");
    }

    public static void ThrowingHandler_SettlesInternal_NotACarrierException()
    {
        using var spine = Spine.Create();
        using var registration = spine.Rpc.Register(new RpcMethod("boom/explode", (_, _) =>
            throw new InvalidOperationException("handler exploded")));
        var response = spine.Rpc.InvokeAsync(new RpcRequest("boom/explode", null)).GetAwaiter().GetResult();
        Assert.False(response.Ok, "the call fails");
        Assert.Equal("gateway/internal", response.Error!.Code);
    }

    public static void Cancellation_SettlesCancelled()
    {
        using var spine = Spine.Create();
        using var registration = spine.Rpc.Register(new RpcMethod("hang/forever", async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }));
        using var cts = new CancellationTokenSource(100);
        var response = spine.Rpc.InvokeAsync(new RpcRequest("hang/forever", null), cts.Token).GetAwaiter().GetResult();
        Assert.False(response.Ok, "the call fails");
        Assert.Equal("gateway/cancelled", response.Error!.Code);
    }

    public static void DuplicateEndpoint_FailsLoud()
    {
        using var spine = Spine.Create();
        using var first = spine.Rpc.Register(new RpcMethod("dup/method", (_, _) => Task.FromResult<JsonElement?>(null)));
        var error = Assert.Throws<ArgumentException>(() => spine.Rpc.Register(new RpcMethod("dup/method", (_, _) => Task.FromResult<JsonElement?>(null))));
        Assert.True(error.Message.Contains("already registered"), "the duplicate endpoint is named");
    }

    public static void EndpointWithoutNamespace_IsRejected()
    {
        using var spine = Spine.Create();
        var error = Assert.Throws<ArgumentException>(() => spine.Rpc.Register(new RpcMethod("bare", (_, _) => Task.FromResult<JsonElement?>(null))));
        Assert.True(error.Message.Contains("namespace/method"), "the endpoint contract is named");
    }

    public static void DisposingTheRegistration_WithdrawsTheMethod()
    {
        using var spine = Spine.Create();
        var registration = spine.Rpc.Register(new RpcMethod("temp/method", (_, _) => Task.FromResult<JsonElement?>(null)));
        Assert.NotNull(spine.Rpc.Get("temp/method"), "the method is live while registered");
        registration.Dispose();
        Assert.Null(spine.Rpc.Get("temp/method"), "the method is withdrawn on dispose");
        var response = spine.Rpc.InvokeAsync(new RpcRequest("temp/method", null)).GetAwaiter().GetResult();
        Assert.Equal("gateway/invocation-unavailable", response.Error!.Code);
    }

    public static void ContextDisposal_WithdrawsEveryMethod()
    {
        var ctx = new Context();
        var rpc = new DshRpcRegistry(ctx);
        _ = rpc.Register(new RpcMethod("temp/one", (_, _) => Task.FromResult<JsonElement?>(null)));
        _ = rpc.Register(new RpcMethod("temp/two", (_, _) => Task.FromResult<JsonElement?>(null)));
        ctx.Dispose();
        var response = rpc.InvokeAsync(new RpcRequest("temp/one", null)).GetAwaiter().GetResult();
        Assert.Equal("gateway/invocation-unavailable", response.Error!.Code);
    }
}

