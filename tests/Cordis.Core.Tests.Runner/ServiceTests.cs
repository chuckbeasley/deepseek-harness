using Harness.Cordis.Core;

namespace Harness.Cordis.Core.Tests.Runner;

internal static class ServiceTests
{
    public static void Service_RegistersItselfAtItsKey()
    {
        using var ctx = new Context();
        var service = new TestService(ctx, "alpha");

        Assert.Same(service, ctx.Get<TestService>("alpha"));
        Assert.Same(service, ctx.Require<TestService>("alpha"));
    }

    public static async Task ContextDispose_UnregistersServiceAndRunsStopAsync()
    {
        var ctx = new Context();
        var service = new TestService(ctx, "alpha");
        Assert.NotNull(ctx.Get<TestService>("alpha"));

        await ctx.DisposeAsync();

        Assert.Null(ctx.Get<TestService>("alpha"));
        Assert.Equal(1, service.StopCount);
    }

    public static void Set_SameInstanceTwice_IsANoOp()
    {
        using var ctx = new Context();
        var service = new TestService(ctx, "alpha");

        ctx.Set("alpha", service); // same instance: no-op, no throw

        Assert.Same(service, ctx.Get<TestService>("alpha"));
    }

    public static void Set_DifferentInstanceUnderExistingKey_Throws()
    {
        using var ctx = new Context();
        var first = new TestService(ctx, "alpha");
        var second = new TestService(ctx, "beta");

        Assert.Throws<InvalidOperationException>(() => ctx.Set("alpha", second));
    }

    public static void Get_WrongType_Throws()
    {
        using var ctx = new Context();
        ctx.Set("alpha", new TestService(ctx, "alpha"));

        Assert.Throws<InvalidOperationException>(() => ctx.Get<OtherService>("alpha"));
    }

    public static void Get_MissingKey_ReturnsNull()
    {
        using var ctx = new Context();
        Assert.Null(ctx.Get<TestService>("missing"));
    }

    public static void Require_MissingKey_Throws()
    {
        using var ctx = new Context();
        Assert.Throws<InvalidOperationException>(() => ctx.Require<TestService>("missing"));
    }

    public static void Service_Dispose_StopsAndUnregisters()
    {
        using var ctx = new Context();
        var service = new TestService(ctx, "alpha");

        service.Dispose();

        Assert.Null(ctx.Get<TestService>("alpha"));
        Assert.Equal(1, service.StopCount);

        // the fiber-level registration effect stays idempotent
        ctx.Dispose();
    }

    public static async Task Registry_TracksServiceKeysAndProvesDisposal()
    {
        var ctx = new Context();
        var service = new TestService(ctx, "alpha");
        var listener = ctx.On("test/reg", new Action(() => { }));
        Assert.Equal(1, ctx.Registry.ListenerCount("test/reg"));
        Assert.Contains("alpha", ctx.Registry.ServiceKeys);

        listener.Dispose();
        await ctx.DisposeAsync();

        Assert.Equal(0, ctx.Registry.ListenerCount("test/reg"));
        Assert.DoesNotContain("alpha", ctx.Registry.ServiceKeys);
    }

    private sealed class TestService : Service
    {
        public int StopCount { get; private set; }

        public TestService(Context ctx, string key) : base(ctx, key) { }

        public override ValueTask StopAsync()
        {
            StopCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OtherService : Service
    {
        public OtherService(Context ctx, string key) : base(ctx, key) { }
    }
}
