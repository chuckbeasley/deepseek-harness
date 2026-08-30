using Cordis.Core;
using Cordis.Plugin.Loader;

namespace Cordis.Plugin.Loader.Tests;

internal sealed class TestService
{
    public TestService(string key, object? value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public object? Value { get; }

    public override string ToString() => $"{Key}={Value}";
}

/// <summary>Plugin that registers a named service and removes it on dispose.</summary>
internal sealed class ProviderPlugin : ILoaderPlugin
{
    private readonly string _service;
    private readonly object? _value;
    private readonly List<string>? _log;

    public ProviderPlugin(string service, object? value, List<string>? log = null)
    {
        _service = service;
        _value = value;
        _log = log;
    }

    public ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config)
    {
        _log?.Add($"apply:{_service}:{_value}");
        var service = new TestService(_service, _value);
        ctx.Set(_service, service);
        return new ValueTask<IDisposable?>(new DisposableAction(() =>
        {
            _log?.Add($"dispose:{_service}:{_value}");
            ctx.Remove(_service);
        }));
    }
}

/// <summary>Plugin whose body always fails; counts instances.</summary>
internal sealed class ThrowPlugin : ILoaderPlugin
{
    public static int Instances;

    public ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config)
    {
        Interlocked.Increment(ref Instances);
        throw new InvalidOperationException($"plugin body failed: {config}");
    }
}

/// <summary>Plugin that records whether its declared dependency was present when it ran.</summary>
internal sealed class ConsumerPlugin : ILoaderPlugin
{
    private readonly string _dependency;
    private readonly string _register;
    private readonly List<string>? _log;

    public ConsumerPlugin(string dependency, string register, List<string>? log = null)
    {
        _dependency = dependency;
        _register = register;
        _log = log;
    }

    public ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config)
    {
        var present = ctx.Get<TestService>(_dependency) is not null;
        _log?.Add($"apply:{_register}:dep={present}");
        var service = new TestService(_register, present);
        ctx.Set(_register, service);
        return new ValueTask<IDisposable?>(new DisposableAction(() => ctx.Remove(_register)));
    }
}

/// <summary>Plugin that records config updates while staying mounted.</summary>
internal sealed class UpdatableRecorder : ILoaderPlugin, IUpdatablePlugin
{
    public List<object?> Updates { get; } = new();

    public ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config) => ValueTask.FromResult<IDisposable?>(null);

    public ValueTask UpdateAsync(object? config)
    {
        Updates.Add(config);
        return ValueTask.CompletedTask;
    }
}

internal sealed class DisposableAction : IDisposable
{
    private readonly Action _action;

    public DisposableAction(Action action)
    {
        _action = action;
    }

    public void Dispose() => _action();
}

/// <summary>Behavior tests for the Phase 1 Cordis loader port.</summary>
public static class LoaderTests
{
    public static async Task OrderedLoad_RegistersServicesInRowOrder()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("alpha", new ProviderPlugin("alpha", "a", log));
        loader.Catalog.Register("beta", new ConsumerPlugin("alpha", "beta", log));

        await loader.Root.UpdateAsync(new[]
        {
            new EntryOptions { Id = "a", Name = "alpha", Config = "c1" },
            new EntryOptions { Id = "b", Name = "beta" },
        });
        await loader.AwaitAsync();

        // both rows mounted in row order; beta saw alpha's service
        Assert.Equal("a", ctx.Get<TestService>("alpha")!.Value);
        Assert.Equal(true, ctx.Get<TestService>("beta")!.Value);
        Assert.Equal("apply:alpha:a", log[0]);
        Assert.Equal("apply:beta:dep=True", log[1]);
    }

    public static async Task DependencyGated_PendingUntilProviderAppears()
    {
        // Scenario A: the consumer is listed before its provider, so it stays pending until the
        // provider registers the service later in the same reconciliation.
        using (var ctx = new Context())
        {
            var loader = new Loader(ctx);
            loader.Catalog.Register("consumer", new ConsumerPlugin("svc", "consumer"));
            loader.Catalog.Register("provider", new ProviderPlugin("svc", "v1"));

            await loader.Root.UpdateAsync(new[]
            {
                new EntryOptions { Id = "c", Name = "consumer", Inject = new[] { "svc" } },
                new EntryOptions { Id = "p", Name = "provider" },
            });

            var consumer = loader.Resolve("c");
            Assert.Equal(FiberState.Pending, consumer.Fiber!.State);
            Assert.Null(ctx.Get<TestService>("consumer"));

            await loader.AwaitAsync();
            Assert.Equal(FiberState.Active, consumer.Fiber!.State);
            Assert.Equal(true, ctx.Get<TestService>("consumer")!.Value);
        }

        // Scenario B: the provider is listed first, so the consumer activates immediately.
        using (var ctx = new Context())
        {
            var loader = new Loader(ctx);
            loader.Catalog.Register("consumer", new ConsumerPlugin("svc", "consumer"));
            loader.Catalog.Register("provider", new ProviderPlugin("svc", "v1"));

            await loader.Root.UpdateAsync(new[]
            {
                new EntryOptions { Id = "p", Name = "provider" },
                new EntryOptions { Id = "c", Name = "consumer", Inject = new[] { "svc" } },
            });
            await loader.AwaitAsync();

            Assert.Equal(FiberState.Active, loader.Resolve("c").Fiber!.State);
            Assert.Equal(true, ctx.Get<TestService>("consumer")!.Value);
        }
    }

    public static async Task ReplaceUpdate_SwapsService()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("v1", new ProviderPlugin("svc", "one", log));
        loader.Catalog.Register("v2", new ProviderPlugin("svc", "two", log));

        await loader.Root.UpdateAsync(new[] { new EntryOptions { Id = "s", Name = "v1" } });
        await loader.AwaitAsync();
        Assert.Equal("one", ctx.Get<TestService>("svc")!.Value);

        // Replacing the plugin imports the candidate before disposing the old row, so the swap
        // is atomic from the service repository's point of view.
        await loader.UpdateAsync("s", new EntryPatch { Name = "v2" });
        await loader.AwaitAsync();

        Assert.Equal("two", ctx.Get<TestService>("svc")!.Value);
        Assert.Contains("dispose:svc:one", log[1]);
        Assert.Equal("apply:svc:two", log[2]);
    }

    public static async Task FailingUpdate_RestoresPreviousRow()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("v1", new ProviderPlugin("svc", "one", log));
        loader.Catalog.Register("boom", new ThrowPlugin());

        await loader.Root.UpdateAsync(new[] { new EntryOptions { Id = "s", Name = "v1" } });
        await loader.AwaitAsync();
        Assert.Equal("one", ctx.Get<TestService>("svc")!.Value);

        var error = Assert.Throws<InvalidOperationException>(() =>
            loader.UpdateAsync("s", new EntryPatch { Name = "boom" }).GetAwaiter().GetResult());
        Assert.Contains("failed to apply loader entry s (boom)", error.Message);

        // The failed candidate was rejected and the previous row restored: same options, same
        // plugin, service back in place.
        var entry = loader.Resolve("s");
        Assert.Equal("v1", entry.Options.Name);
        Assert.NotNull(entry.Fiber);
        Assert.Equal(FiberState.Active, entry.Fiber!.State);
        Assert.Equal("one", ctx.Get<TestService>("svc")!.Value);
        Assert.Contains("dispose:svc:one", log);
    }

    public static async Task DisposalUnwind_DisposesRowsAndServices()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("a", new ProviderPlugin("a", 1, log));
        loader.Catalog.Register("b", new ProviderPlugin("b", 2, log));

        await loader.Root.UpdateAsync(new[]
        {
            new EntryOptions { Id = "x", Name = "a" },
            new EntryOptions { Id = "y", Name = "b" },
        });
        await loader.AwaitAsync();
        Assert.NotNull(ctx.Get<TestService>("a"));
        Assert.NotNull(ctx.Get<TestService>("b"));

        await loader.DisposeAsync();

        Assert.Null(ctx.Get<TestService>("a"));
        Assert.Null(ctx.Get<TestService>("b"));
        Assert.Contains("dispose:a:1", log);
        Assert.Contains("dispose:b:2", log);
        // the vendored EntryGroup.stop disposes rows in mount order and empties the store;
        // the data list keeps the row options (isDispose skips unlink, matching the TS)
        Assert.True(log.IndexOf("dispose:a:1") < log.IndexOf("dispose:b:2"));
        Assert.Equal(0, loader.Store.Count);
        Assert.Equal(2, loader.Root.Data.Count);
    }

    public static async Task GroupReconciliation_RollsBackFailedCandidate()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("good", new ProviderPlugin("svc", "ok", log));
        loader.Catalog.Register("bad", new ThrowPlugin());

        await loader.CreateAsync(new EntryOptions
        {
            Id = "g",
            Name = "cordis:group",
            Group = true,
            Config = new List<EntryOptions> { new() { Id = "c1", Name = "good" } },
        });
        await loader.AwaitAsync();
        Assert.Equal("ok", ctx.Get<TestService>("svc")!.Value);

        // A failed candidate inside the group rolls the group update back to its previous rows.
        var error = Assert.Throws<InvalidOperationException>(() => loader.Root.UpdateAsync(new[]
        {
            new EntryOptions
            {
                Id = "g",
                Name = "cordis:group",
                Group = true,
                Config = new List<EntryOptions>
                {
                    new() { Id = "c1", Name = "good" },
                    new() { Id = "bad", Name = "bad" },
                },
            },
        }).GetAwaiter().GetResult());
        Assert.Contains("failed to apply loader entry g", error.Message);

        // the surviving child still runs, the failed candidate is gone, and the group rows
        // are back to the previous set
        Assert.Equal("ok", ctx.Get<TestService>("svc")!.Value);
        Assert.False(loader.Store.ContainsKey("bad"));
        var group = loader.Resolve("g");
        Assert.Equal(1, group.Subgroup!.Data.Count);
        Assert.Equal("c1", group.Subgroup.Data[0].Id);
    }

    public static async Task ConfigOnlyUpdate_NotifiesUpdatablePlugin()
    {
        using var ctx = new Context();
        var recorder = new UpdatableRecorder();
        var loader = new Loader(ctx);
        loader.Catalog.Register("up", recorder);

        await loader.Root.UpdateAsync(new[] { new EntryOptions { Id = "u", Name = "up", Config = "c1" } });
        await loader.AwaitAsync();

        await loader.UpdateAsync("u", new EntryPatch { Config = "c2" });

        Assert.Equal(1, recorder.Updates.Count);
        Assert.Equal("c2", recorder.Updates[0]);
        Assert.Equal("c2", loader.Resolve("u").Options.Config);
        Assert.Equal(FiberState.Active, loader.Resolve("u").Fiber!.State);
    }

    public static async Task DisabledRow_DoesNotMount()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("provider", new ProviderPlugin("svc", 1, log));

        await loader.Root.UpdateAsync(new[]
        {
            new EntryOptions { Id = "off", Name = "provider", Disabled = true },
        });
        await loader.AwaitAsync();

        Assert.True(loader.Resolve("off").Disabled);
        Assert.Null(loader.Resolve("off").Fiber);
        Assert.Null(ctx.Get<TestService>("svc"));
        Assert.Equal(0, log.Count);
    }

    public static async Task DuplicateRowId_IsRejected()
    {
        using var ctx = new Context();
        var loader = new Loader(ctx);

        var error = Assert.Throws<InvalidOperationException>(() => loader.Root.UpdateAsync(new[]
        {
            new EntryOptions { Id = "dup", Name = "x" },
            new EntryOptions { Id = "dup", Name = "y" },
        }).GetAwaiter().GetResult());
        Assert.Contains("duplicate loader entry id: dup", error.Message);
    }

    public static async Task GroupRow_ComposesNestedTree()
    {
        using var ctx = new Context();
        var log = new List<string>();
        var loader = new Loader(ctx);
        loader.Catalog.Register("provider", new ProviderPlugin("svc", "kid", log));

        var groupId = await loader.CreateAsync(new EntryOptions
        {
            Id = "g",
            Name = "cordis:group",
            Group = true,
            Config = new List<EntryOptions> { new() { Id = "kid", Name = "provider" } },
        });
        await loader.AwaitAsync();

        // the group row owns a subgroup; children get composite ids and run
        Assert.Equal("g", groupId);
        var kid = loader.Store["kid"];
        Assert.Equal("g:kid", kid.Id);
        Assert.Equal("kid", ctx.Get<TestService>("svc")!.Value);
        Assert.Equal("g", loader.ResolveGroup("g").OwnerEntry!.Options.Id);
        Assert.Equal(FiberState.Active, loader.Resolve("g").Fiber!.State);
    }
}
