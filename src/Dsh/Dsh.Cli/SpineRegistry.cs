using Cordis.Core;
using Cordis.Plugin.Loader;

namespace Dsh.Cli;

/// <summary>
/// The row-name to service-constructor registry the profile boot mounts (the C# spine equivalent
/// of the TS resolver manifest's in-box names). Each row registers one <see cref="ILoaderPlugin"/>
/// whose <c>ApplyAsync</c> constructs the service on the shared context and returns the disposer
/// that removes it. Rows the spine does not know fail loud at boot with the row id.
/// </summary>
public static class SpineRegistry
{
    /// <summary>Register every spine row on <paramref name="catalog"/>.</summary>
    public static void RegisterAll(PluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register("sessions", new SpinePlugin("sessions", (ctx, _) => new Dsh.Session.SessionStore(ctx)));
        catalog.Register("llm", new SpinePlugin("llm", (ctx, _) => new Dsh.Llm.LlmRuntime(ctx)));
        catalog.Register("tools", new SpinePlugin("tools", (ctx, _) => new Dsh.Tools.ToolRuntime(ctx)));
        catalog.Register("systemPrompt", new SpinePlugin("systemPrompt", (ctx, _) => new Dsh.SystemPrompt.SystemPromptService(ctx)));
        catalog.Register("agents", new SpinePlugin("agents", (ctx, _) => new Dsh.Agent.AgentRegistry(ctx)));
        catalog.Register("agentLoop", new SpinePlugin("agentLoop", (ctx, _) => new Dsh.AgentLoop.AgentLoop(ctx)));
        catalog.Register("sessionPersistence", new SpinePlugin("sessionPersistence", (ctx, config) =>
        {
            var root = ConfigString(config, "root")
                ?? Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "sessions");
            var persistence = new Dsh.Session.Persistence.SessionPersistenceService(ctx, new Dsh.Session.Persistence.PersistenceConfig { Root = root });
            var sessions = ctx.Get<Dsh.Session.SessionStore>("sessions")
                ?? throw new InvalidOperationException("sessionPersistence requires the \"sessions\" row");
            var attach = persistence.Attach(sessions);
            return new SpineDisposables(persistence, attach);
        }));
        catalog.Register("todo", new SpinePlugin("todo", (ctx, config) =>
        {
            var parallel = ConfigBool(config, "allowParallelInProgress") ?? false;
            return new ToolPluginRegistration(ctx.Tools().Register(Dsh.Spike.TodoTool.Definition(parallel)));
        }));
        catalog.Register("mock", new SpinePlugin("mock", (ctx, _) =>
            ctx.Llm().RegisterAdapter(new[] { Dsh.Spike.MockLlmProvider.Provider }, new Dsh.Spike.MockLlmProvider())));
        catalog.Register("deepseek", new SpinePlugin("deepseek", (ctx, config) =>
        {
            var key = ConfigString(config, "apiKey") ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrEmpty(key)) return null; // keyless boots run the mock route
            var adapter = new Dsh.Llm.DeepSeek.DeepSeekAdapter(new Dsh.Llm.DeepSeek.DeepSeekConfig
            {
                ApiKey = key,
                BaseUrl = ConfigString(config, "baseUrl"),
            });
            return ctx.Llm().RegisterAdapter(new[] { "deepseek" }, adapter);
        }));
        catalog.Register("headless", new SpinePlugin("headless", (ctx, config) =>
        {
            var run = new HeadlessRun();
            var task = run.ApplyAsync(ctx, config);
            return task.AsTask().GetAwaiter().GetResult();
        }));
    }

    private static string? ConfigString(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is string text
            ? text
            : null;

    private static bool? ConfigBool(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is bool boolean
            ? boolean
            : null;
}

/// <summary>Disposes several disposers in order (service last, so its teardown runs first).</summary>
internal sealed class SpineDisposables : IDisposable
{
    private readonly IDisposable[] _disposers;

    public SpineDisposables(params IDisposable[] disposers)
    {
        _disposers = disposers;
    }

    public void Dispose()
    {
        foreach (var disposer in _disposers) disposer.Dispose();
    }
}

/// <summary>Adapter holding the tool-registry disposer behind an <see cref="IDisposable"/>.</summary>
internal sealed class ToolPluginRegistration : IDisposable
{
    private readonly IDisposable _disposer;

    public ToolPluginRegistration(IDisposable disposer)
    {
        _disposer = disposer;
    }

    public void Dispose() => _disposer.Dispose();
}

/// <summary>One spine row: a factory that builds the service and returns its removal disposer.</summary>
internal sealed class SpinePlugin : ILoaderPlugin
{
    private readonly string _name;
    private readonly Func<Context, object?, IDisposable?> _apply;

    public SpinePlugin(string name, Func<Context, object?, IDisposable?> apply)
    {
        _name = name;
        _apply = apply;
    }

    public ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config)
    {
        IDisposable? disposer;
        try
        {
            disposer = _apply(ctx, config);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"dsh: spine row \"{_name}\" failed to load: {error.Message}", error);
        }
        return ValueTask.FromResult(disposer);
    }
}

/// <summary>Typed service lookups used by the spine rows.</summary>
internal static class SpineContextExtensions
{
    public static Dsh.Llm.LlmRuntime Llm(this Context ctx)
        => ctx.Get<Dsh.Llm.LlmRuntime>("llm") ?? throw new InvalidOperationException("spine row requires the \"llm\" service");

    public static Dsh.Tools.ToolRuntime Tools(this Context ctx)
        => ctx.Get<Dsh.Tools.ToolRuntime>("tools") ?? throw new InvalidOperationException("spine row requires the \"tools\" service");
}
