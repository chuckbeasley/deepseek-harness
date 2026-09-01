using Harness.Cordis.Core;

namespace Harness.Cordis.Core.Tests.Runner;

internal static class FiberEffectTests
{
    public static async Task DisposeAsync_UnwindsEffectsInReverseRegistrationOrder()
    {
        var ctx = new Context();
        var order = new List<string>();
        ctx.Effect(() => new Disposer(() => order.Add("a")));
        ctx.Effect(() => new Disposer(() => order.Add("b")));
        ctx.Effect(() => new Disposer(() => order.Add("c")));

        await ctx.DisposeAsync();

        Assert.Equal(new[] { "c", "b", "a" }, order);
    }

    public static async Task FailingCleanup_IsContainedAndDoesNotStarvePeers()
    {
        var ctx = new Context();
        var ran = new List<string>();
        ctx.Effect(() => new Disposer(() => { ran.Add("a"); throw new InvalidOperationException("a fails"); }));
        ctx.Effect(() => new Disposer(() => ran.Add("b")));
        ctx.Effect(() => new Disposer(() => ran.Add("c")));

        await ctx.DisposeAsync(); // must not throw

        Assert.Equal(new[] { "c", "b", "a" }, ran);
    }

    public static void EffectDisposer_IsSingleShot()
    {
        using var ctx = new Context();
        var count = 0;
        var disposer = ctx.Effect(() => new Disposer(() => count++));

        disposer.Dispose();
        disposer.Dispose();

        Assert.Equal(1, count);
    }

    public static async Task Effect_DisposedBeforeUnload_IsNotRunAgainAtContextDispose()
    {
        var ctx = new Context();
        var count = 0;
        var disposer = ctx.Effect(() => new Disposer(() => count++));

        disposer.Dispose();
        await ctx.DisposeAsync();

        Assert.Equal(1, count);
    }

    public static async Task Effect_OnDisposedContext_ThrowsInactiveEffect()
    {
        var ctx = new Context();
        await ctx.DisposeAsync();

        Assert.Throws<CordisError>(() => ctx.Effect(() => new Disposer(() => { })));
    }

    public static async Task NestedEffect_UnwindsBeforeItsParent()
    {
        var ctx = new Context();
        var order = new List<string>();
        ctx.Effect(() =>
        {
            ctx.Effect(() => new Disposer(() => order.Add("child")));
            return new Disposer(() => order.Add("parent"));
        });

        await ctx.DisposeAsync();

        Assert.Equal(new[] { "child", "parent" }, order);
    }

    public static async Task EffectAsync_AwaitsAsyncCleanup()
    {
        var ctx = new Context();
        var finished = false;
        var disposer = ctx.EffectAsync(() => new AsyncDisposer(async () =>
        {
            await Task.Yield();
            finished = true;
        }));

        await disposer.DisposeAsync();

        Assert.True(finished);
    }

    public static async Task ContextDispose_UnwindsListenerRegistrations()
    {
        var ctx = new Context();
        var count = 0;
        ctx.On("test/lifecycle", new Action(() => count++));

        ctx.Emit("test/lifecycle");
        await ctx.DisposeAsync();
        ctx.Emit("test/lifecycle");

        Assert.Equal(1, count);
        Assert.Equal(0, ctx.Events.ListenerCount("test/lifecycle"));
    }

    private sealed class Disposer : IDisposable
    {
        private readonly Action _action;

        public Disposer(Action action) => _action = action;

        public void Dispose() => _action();
    }

    private sealed class AsyncDisposer : IAsyncDisposable
    {
        private readonly Func<ValueTask> _action;

        public AsyncDisposer(Func<ValueTask> action) => _action = action;

        public ValueTask DisposeAsync() => _action();
    }
}
