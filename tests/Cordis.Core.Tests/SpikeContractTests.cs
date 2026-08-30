using Cordis.Core;
using Xunit;

namespace Cordis.Core.Tests;

/// <summary>
/// Proves the section 7 consumer contract of .wt/spike-slice/spike-design.md compiles against the
/// port: Set/Get/Require, Effect(Func&lt;IDisposable&gt;), On/Emit, Waterfall with args + next,
/// Logger, DisposeAsync, and the Service base registering itself at its key.
/// </summary>
public class SpikeContractTests
{
    private sealed class SamplePayload
    {
        public required string Text { get; init; }
    }

    private sealed class SampleService : Service
    {
        public SampleService(Context ctx, string key) : base(ctx, key) { }
    }

    private sealed class Disposer : IDisposable
    {
        private readonly Action _action;

        public Disposer(Action action) => _action = action;

        public void Dispose() => _action();
    }

    [Fact]
    public async Task SpikeSection7_Surface_CompilesAndRuns()
    {
        using var ctx = new Context(); // boot a root context
        ctx.Set("samples", new SampleService(ctx, "samples")); // register a service
        Assert.NotNull(ctx.Get<SampleService>("samples")); // strict read
        Assert.NotNull(ctx.Require<SampleService>("samples")); // fail-loud read

        // reversible effect: register() returns the disposer
        var unwound = false;
        using (var effect = ctx.Effect(() => new Disposer(() => unwound = true)))
        {
            Assert.False(unwound);
        }
        Assert.True(unwound);

        // on/emit with a strongly typed payload object under a string-named key
        SamplePayload? seen = null;
        ctx.On("sample/created", (SamplePayload payload) => seen = payload);
        ctx.Emit("sample/created", new SamplePayload { Text = "hi" });
        Assert.Equal("hi", seen?.Text);

        // waterfall: args + next; values propagate through next()'s return
        ctx.On("sample/stream", new Func<SamplePayload, Func<int>, int>((payload, next) => payload.Text.Length + next()));
        var result = ctx.Waterfall<int>("sample/stream", new object?[] { new SamplePayload { Text = "abc" } }, () => 1);
        Assert.Equal(4, result);

        // logger for containment diagnostics
        ctx.Logger.Warn("observer failed");

        await ctx.DisposeAsync();
        Assert.Null(ctx.Get<SampleService>("samples"));
    }

    [Fact]
    public void On_AcceptsLambdaWithExplicitParameterTypes()
    {
        using var ctx = new Context();
        string? seen = null;

        // the raw Delegate overload accepts a lambda with explicit parameter types
        ctx.On("test/lambda", (string payload) => seen = payload);
        ctx.Emit("test/lambda", "hi");

        Assert.Equal("hi", seen);
    }
}
