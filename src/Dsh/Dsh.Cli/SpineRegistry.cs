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
            var service = new Dsh.Todo.TodoService(ctx, parallel);
            var registration = ctx.Tools().Register(Dsh.Todo.TodoTool.Definition(ctx, parallel));
            return new SpineDisposables(registration, service);
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
        catalog.Register("subprocess", new SpinePlugin("subprocess", (ctx, _) => new Dsh.Subprocess.LocalSubprocessProvider(ctx)));
        catalog.Register("fs", new SpinePlugin("fs", (ctx, config) =>
        {
            var root = ConfigString(config, "root") ?? Environment.CurrentDirectory;
            var service = new Dsh.Fs.LocalFileSystemProvider(ctx, new Dsh.Fs.FsProviderConfig(root));
            var read = ctx.Tools().Register(Dsh.Fs.FileSystemTools.Read(service));
            var write = ctx.Tools().Register(Dsh.Fs.FileSystemTools.Write(service));
            return new SpineDisposables(write, read, service);
        }));
        catalog.Register("shell", new SpinePlugin("shell", (ctx, config) =>
        {
            var service = new Dsh.Shell.LocalShellProvider(ctx, new Dsh.Shell.ShellConfig
            {
                ShellPath = ConfigString(config, "shellPath") ?? (OperatingSystem.IsWindows() ? "cmd.exe" : "sh"),
                TimeoutMs = ConfigInt(config, "timeoutMs") ?? 120000,
                StdoutMaxBytes = ConfigInt(config, "stdoutMaxBytes") ?? 256 * 1024,
            });
            var registration = ctx.Tools().Register(Dsh.Shell.ShellTools.Definition(ctx));
            return new SpineDisposables(registration, service);
        }));
        catalog.Register("identity", new SpinePlugin("identity", (ctx, _) => Dsh.Identity.AnonymousIdentityProvider.Create(ctx)));
        catalog.Register("plan", new SpinePlugin("plan", (ctx, _) =>
        {
            var service = new Dsh.Plan.SessionPlanService(ctx);
            var registration = ctx.Tools().Register(Dsh.Plan.PlanTools.Definition());
            return new SpineDisposables(registration, service);
        }));
        catalog.Register("web", new SpinePlugin("web", (ctx, _) =>
        {
            var web = new Dsh.Web.WebRuntime(ctx);
            var fetchRegistration = web.RegisterFetchProvider(new Dsh.Web.HttpWebProvider());
            var fetchTool = ctx.Tools().Register(Dsh.Web.WebTools.WebFetchDefinition(web));
            var searchTool = ctx.Tools().Register(Dsh.Web.WebTools.WebSearchDefinition(web));
            return new SpineDisposables(searchTool, fetchTool, fetchRegistration, web);
        }));
        catalog.Register("credentials", new SpinePlugin("credentials", (ctx, _) => new Dsh.Credentials.LocalCredentialsProvider(ctx)));
        catalog.Register("goal", new SpinePlugin("goal", (ctx, _) =>
        {
            var service = new Dsh.Goal.SessionGoalService(ctx);
            var registration = ctx.Tools().Register(Dsh.Goal.GoalTools.Definition(service));
            return new SpineDisposables(registration, service);
        }));
        catalog.Register("schedule", new SpinePlugin("schedule", (ctx, _) =>
        {
            // The schedule seam requires the timer service; mount it here so a profile need not
            // name two rows for one capability.
            var timer = ctx.Get<Cordis.Plugin.Timer.TimerService>("timer") ?? new Cordis.Plugin.Timer.TimerService(ctx);
            return new SpineDisposables(new Dsh.Schedule.TimerScheduleProvider(ctx), timer);
        }));
        catalog.Register("feedback", new SpinePlugin("feedback", (ctx, _) =>
        {
            var service = new Dsh.Feedback.SessionFeedbackService(ctx);
            var registration = ctx.Tools().Register(Dsh.Feedback.FeedbackTools.Definition(service));
            return new SpineDisposables(registration, service);
        }));
        catalog.Register("storage", new SpinePlugin("storage", (ctx, config) =>
        {
            var root = ConfigString(config, "root")
                ?? Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "storage");
            return new Dsh.Storage.JsonFileStorageProvider(ctx, new Dsh.Storage.JsonFileStorageConfig(root));
        }));
        catalog.Register("workspace", new SpinePlugin("workspace", (ctx, _) => new Dsh.Workspace.LocalWorkspaceProvider(ctx)));
        catalog.Register("spill", new SpinePlugin("spill", (ctx, config) =>
        {
            var root = ConfigString(config, "root")
                ?? Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "spill");
            return new Dsh.Spill.LocalSpillProvider(ctx, new Dsh.Spill.SpillProviderConfig(root));
        }));
        catalog.Register("attachment", new SpinePlugin("attachment", (ctx, config) =>
        {
            var root = ConfigString(config, "root")
                ?? Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "attachments");
            var maxBytes = ConfigInt(config, "maxBytes") ?? 10 * 1024 * 1024;
            return new Dsh.Attachment.LocalAttachmentProvider(ctx, new Dsh.Attachment.AttachmentProviderConfig(root, maxBytes));
        }));
        catalog.Register("compaction", new SpinePlugin("compaction", (ctx, _) => new Dsh.Compaction.BasicCompactionProvider(ctx)));
        catalog.Register("context", new SpinePlugin("context", (ctx, _) => new Dsh.Context.LocalContextProvider(ctx)));
        catalog.Register("sessionQuery", new SpinePlugin("sessionQuery", (ctx, _) => new Dsh.SessionQuery.LogSessionQueryProvider(ctx)));
        catalog.Register("preset", new SpinePlugin("preset", (ctx, _) =>
        {
            var root = Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "presets");
            var provider = new Dsh.Preset.FilePresetProvider(root);
            ctx.Set("preset", provider);
            return null;
        }));
        catalog.Register("guard", new SpinePlugin("guard", (ctx, _) =>
            new SpineDisposables(new Dsh.Guard.ToolTimeoutPolicy(ctx), new Dsh.Guard.RepeatToolReminderGuard(ctx))));
        catalog.Register("terminal", new SpinePlugin("terminal", (ctx, _) =>
        {
            var service = new Dsh.Terminal.LocalTerminalProvider(ctx);
            var tools = Dsh.Terminal.TerminalTools.Definitions(ctx);
            var disposers = tools.Select(ctx.Tools().Register).ToArray();
            return new SpineDisposables(disposers.Append(service).ToArray());
        }));
        catalog.Register("subagent", new SpinePlugin("subagent", (ctx, _) => new Dsh.Subagent.InProcessSubagentProvider(ctx)));
        catalog.Register("tui", new SpinePlugin("tui", (ctx, _) =>
        {
            var args = ctx.Get<CmdlineArgs>("cmdlineArgs") ?? new CmdlineArgs(Array.Empty<string>());
            var code = Dsh.Tui.TuiApp.Run(args.Args.ToArray());
            var exit = ctx.Get<AppExit>("appExit")
                ?? throw new InvalidOperationException("dsh: tui requires the appExit launcher fact");
            exit.Exit(code);
            return null;
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

    private static int? ConfigInt(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is long integer
            ? (int)integer
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

/// <summary>One spine row: a factory that builds the service and returns its removal disposer.</summary>
internal sealed class SpinePlugin : ILoaderPlugin
{
    private readonly string _name;
    private readonly Func<Cordis.Core.Context, object?, IDisposable?> _apply;

    public SpinePlugin(string name, Func<Cordis.Core.Context, object?, IDisposable?> apply)
    {
        _name = name;
        _apply = apply;
    }

    public ValueTask<IDisposable?> ApplyAsync(Cordis.Core.Context ctx, object? config)
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
    public static Dsh.Llm.LlmRuntime Llm(this Cordis.Core.Context ctx)
        => ctx.Get<Dsh.Llm.LlmRuntime>("llm") ?? throw new InvalidOperationException("spine row requires the \"llm\" service");

    public static Dsh.Tools.ToolRuntime Tools(this Cordis.Core.Context ctx)
        => ctx.Get<Dsh.Tools.ToolRuntime>("tools") ?? throw new InvalidOperationException("spine row requires the \"tools\" service");
}
