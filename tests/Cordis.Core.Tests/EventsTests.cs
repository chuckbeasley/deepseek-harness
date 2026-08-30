using Cordis.Core;
using Xunit;

namespace Cordis.Core.Tests;

public class EventsTests
{
    [Fact]
    public void Emit_CallsListenersInRegistrationOrder()
    {
        using var ctx = new Context();
        var order = new List<string>();
        ctx.On("test/emit", new Action(() => order.Add("first")));
        ctx.On("test/emit", new Action(() => order.Add("second")));
        ctx.On("test/emit", new Action(() => order.Add("third")));

        ctx.Emit("test/emit");

        Assert.Equal(new[] { "first", "second", "third" }, order);
    }

    [Fact]
    public void Emit_DeliversPayloadToTypedListener()
    {
        using var ctx = new Context();
        string? seen = null;
        ctx.On<string>("test/payload", payload => seen = payload);

        ctx.Emit("test/payload", "hello");

        Assert.Equal("hello", seen);
    }

    [Fact]
    public void Emit_ThrowingListener_PropagatesAndAbortsRemaining()
    {
        using var ctx = new Context();
        var reached = false;
        ctx.On("test/throw", new Action(() => throw new InvalidOperationException("boom")));
        ctx.On("test/throw", new Action(() => reached = true));

        Assert.Throws<InvalidOperationException>(() => ctx.Emit("test/throw"));
        Assert.False(reached);
    }

    [Fact]
    public async Task Parallel_AwaitsEveryListenerAndAggregatesFailures()
    {
        using var ctx = new Context();
        var ran = 0;
        ctx.On("test/parallel", new Func<Task>(() => { Interlocked.Increment(ref ran); return Task.Delay(1); }));
        ctx.On("test/parallel", new Func<Task>(() => { Interlocked.Increment(ref ran); throw new InvalidOperationException("f1"); }));
        ctx.On("test/parallel", new Func<Task>(() => { Interlocked.Increment(ref ran); throw new ArgumentException("f2"); }));

        var error = await Assert.ThrowsAsync<AggregateException>(() => ctx.Parallel("test/parallel"));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(3, ran);
    }

    [Fact]
    public async Task Serial_AwaitsInOrderAndReturnsFirstBailValue()
    {
        using var ctx = new Context();
        var order = new List<string>();
        ctx.On("test/serial", new Func<Task<object?>>(() => { order.Add("a"); return Task.FromResult<object?>(null); }));
        ctx.On("test/serial", new Func<Task<object?>>(() => { order.Add("b"); return Task.FromResult<object?>("bailed"); }));
        ctx.On("test/serial", new Func<Task<object?>>(() => { order.Add("c"); return Task.FromResult<object?>(null); }));

        var result = await ctx.Serial("test/serial");

        Assert.Equal("bailed", result);
        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void Bail_StopsAtFirstBailValue()
    {
        using var ctx = new Context();
        var order = new List<string>();
        ctx.On("test/bail", new Func<object?>(() => { order.Add("a"); return null; }));
        ctx.On("test/bail", new Func<object?>(() => { order.Add("b"); return false; }));
        ctx.On("test/bail", new Func<object?>(() => { order.Add("c"); return "bailed"; }));
        ctx.On("test/bail", new Func<object?>(() => { order.Add("d"); return null; }));

        var result = ctx.Bail("test/bail");

        Assert.Equal("bailed", result);
        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void Waterfall_ShortCircuit_ReturnsListenerValueWithoutCallingNext()
    {
        using var ctx = new Context();
        var innerCalled = false;
        ctx.On("test/wf", new Func<Func<int>, int>(next => 7));

        var result = ctx.Waterfall<int>("test/wf", () => { innerCalled = true; return 42; });

        Assert.Equal(7, result);
        Assert.False(innerCalled);
    }

    [Fact]
    public void Waterfall_NextReturns_PropagateThroughTheChain()
    {
        using var ctx = new Context();
        ctx.On("test/wf", new Func<Func<int>, int>(next => next() + 100));
        ctx.On("test/wf", new Func<Func<int>, int>(next => next() + 10));

        var result = ctx.Waterfall<int>("test/wf", () => 1);

        Assert.Equal(111, result);
    }

    [Fact]
    public void Waterfall_Veto_StopsDownstreamTransformers()
    {
        using var ctx = new Context();
        var downstreamRan = false;
        ctx.On("test/wf", new Func<Func<int>, int>(next => next() + 5));
        ctx.On("test/wf", new Func<Func<int>, int>(next => 1000)); // vetoes: never calls next()
        ctx.On("test/wf", new Func<Func<int>, int>(next => { downstreamRan = true; return next() + 1; }));

        var result = ctx.Waterfall<int>("test/wf", () => 1);

        Assert.Equal(1005, result);
        Assert.False(downstreamRan);
    }

    [Fact]
    public void Waterfall_DeliversEventArgumentsToListeners()
    {
        using var ctx = new Context();
        ctx.On("test/wf", new Func<string, Func<int>, int>((payload, next) => payload.Length + next()));

        var result = ctx.Waterfall<int>("test/wf", new object?[] { "hello" }, () => 1);

        Assert.Equal(6, result);
    }

    [Fact]
    public void Once_RemovesListenerAfterFirstInvocation()
    {
        using var ctx = new Context();
        var count = 0;
        ctx.Once("test/once", new Action(() => count++));

        ctx.Emit("test/once");
        ctx.Emit("test/once");

        Assert.Equal(1, count);
        Assert.Equal(0, ctx.Events.ListenerCount("test/once"));
    }

    [Fact]
    public void On_DisposerRemovesTheRegistration()
    {
        using var ctx = new Context();
        var count = 0;
        var disposer = ctx.On("test/removal", new Action(() => count++));

        ctx.Emit("test/removal");
        disposer.Dispose();
        ctx.Emit("test/removal");
        disposer.Dispose(); // single-shot: no-op

        Assert.Equal(1, count);
        Assert.Equal(0, ctx.Events.ListenerCount("test/removal"));
    }

    [Fact]
    public void Prepend_AddsListenerBeforeExisting()
    {
        using var ctx = new Context();
        var order = new List<string>();
        ctx.On("test/prepend", new Action(() => order.Add("first")));
        ctx.On("test/prepend", new Action(() => order.Add("second")), new EventOptions(Prepend: true));

        ctx.Emit("test/prepend");

        Assert.Equal(new[] { "second", "first" }, order);
    }
}
