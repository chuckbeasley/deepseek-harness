using System.Text.Json;
using Cordis.Core;
using Cordis.Plugin.Loader;
using Dsh.Web.App;

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
        catalog.Register("sessionProjections", new SpinePlugin("sessionProjections", (ctx, _) => new Dsh.Session.Projection.SessionProjectionRegistry(ctx)));
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
        catalog.Register("approval", new SpinePlugin("approval", (ctx, config) =>
        {
            var policy = ConfigString(config, "policy") switch
            {
                null or "ask" => Dsh.Interaction.ApprovalPolicy.Ask,
                "never" => Dsh.Interaction.ApprovalPolicy.Never,
                var value => throw new InvalidOperationException($"approval policy must be \"ask\" or \"never\", got \"{value}\""),
            };
            return new Dsh.Interaction.ApprovalService(ctx, policy);
        }));
        catalog.Register("userQuestions", new SpinePlugin("userQuestions", (ctx, _) => new Dsh.Interaction.UserQuestionService(ctx)));
        catalog.Register("toolAskUser", new SpinePlugin("toolAskUser", (ctx, _) =>
        {
            var tools = ctx.Get<Dsh.Tools.ToolRuntime>("tools");
            return tools is null ? null : tools.Register(Dsh.Interaction.AskUserTool.Definition(ctx));
        }));
        catalog.Register("uiSidebar", new SpinePlugin("uiSidebar", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            return slots is null ? null : Dsh.Ui.Sidebar.UiSidebarPlugin.Apply(slots);
        }));
        catalog.Register("uiSessions", new SpinePlugin("uiSessions", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            return slots is null ? null : Dsh.Ui.Sessions.UiSessionsPlugin.Apply(slots);
        }));
        catalog.Register("uiChat", new SpinePlugin("uiChat", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            return slots is null ? null : Dsh.Ui.Chat.UiChatPlugin.Apply(slots);
        }));
        catalog.Register("uiApproval", new SpinePlugin("uiApproval", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            return slots is null ? null : Dsh.Ui.Approval.UiApprovalPlugin.Apply(slots);
        }));
        catalog.Register("uiSettings", new SpinePlugin("uiSettings", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            var assemblies = ctx.Get<Dsh.Web.App.PageAssemblyRegistry>("pageAssemblies");
            return slots is null || assemblies is null ? null : Dsh.Ui.Settings.UiSettingsPlugin.Apply(slots, assemblies);
        }));
        catalog.Register("uiPlan", new SpinePlugin("uiPlan", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            var assemblies = ctx.Get<Dsh.Web.App.PageAssemblyRegistry>("pageAssemblies");
            return slots is null || assemblies is null ? null : Dsh.Ui.Plan.UiPlanPlugin.Apply(slots, assemblies);
        }));
        catalog.Register("uiWorkspace", new SpinePlugin("uiWorkspace", (ctx, _) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            return slots is null ? null : Dsh.Ui.Workspace.UiWorkspacePlugin.Apply(slots);
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
        catalog.Register("replay", new SpinePlugin("replay", (ctx, _) =>
        {
            // The snapshot-test LLM replay row: serves the recorded fixture streams keylessly.
            // The fixture path is required (Config.file or $DSH_SNAPSHOT_FILE) and the provider
            // route follows the recorded request header ($DSH_SNAPSHOT_PROVIDER).
            var file = ConfigString(_, "file") ?? Environment.GetEnvironmentVariable(Dsh.Llm.Replay.SnapshotEnv.FileEnv);
            if (string.IsNullOrEmpty(file))
            {
                throw new InvalidOperationException(
                    "replay requires a fixture path (Config.file or $DSH_SNAPSHOT_FILE)");
            }
            var overrideFile = ConfigString(_, "overrideFile")
                ?? Environment.GetEnvironmentVariable(Dsh.Llm.Replay.SnapshotEnv.OverrideEnv);
            var childEnv = Environment.GetEnvironmentVariable(Dsh.Llm.Replay.SnapshotEnv.ChildFilesEnv);
            var childFiles = childEnv is { Length: > 0 }
                ? childEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                : null;
            var handle = Dsh.Llm.Replay.ReplayInstall.Install(ctx.Llm(), new Dsh.Llm.Replay.ReplayConfig
            {
                File = file,
                OverrideFile = string.IsNullOrEmpty(overrideFile) ? null : overrideFile,
                ChildFiles = childFiles,
                Provider = Dsh.Llm.Replay.SnapshotEnv.Provider,
                Models = ParseModelMetadata(Environment.GetEnvironmentVariable("DSH_SNAPSHOT_MODEL_META")),
            });
            // The end-of-run consumption check turns a fixture underrun into a crisp exit-1
            // diagnostic; the CLI owns process lifetime, so dispose runs on ctx disposal.
            return new SpineDisposables(new CallbackDisposable(() =>
            {
                try
                {
                    handle.AssertConsumed();
                }
                finally
                {
                    handle.Dispose();
                }
            }), handle);
        }));
        catalog.Register("policyBaseline", new SpinePlugin("policyBaseline", (ctx, config) =>
        {
            var plugin = new PolicyBaselinePlugin();
            var task = plugin.ApplyAsync(ctx, config);
            return task.AsTask().GetAwaiter().GetResult();
        }));
        catalog.Register("policyContext", new SpinePlugin("policyContext", (ctx, config) =>
        {
            var plugin = new PolicyContextPlugin();
            var task = plugin.ApplyAsync(ctx, config);
            return task.AsTask().GetAwaiter().GetResult();
        }));
        catalog.Register("sessionTitle", new SpinePlugin("sessionTitle", (ctx, config) =>
        {
            var service = new Dsh.Session.Titles.FallbackSessionTitleService(ctx, new Dsh.Session.Titles.SessionTitleConfig
            {
                FallbackMaxWords = ConfigInt(config, "fallbackMaxWords") ?? 5,
                FallbackMaxBytes = ConfigInt(config, "fallbackMaxBytes") ?? 40,
                MaxTitleBytes = ConfigInt(config, "maxTitleBytes") ?? 80,
            });
            return service;
        }));
        catalog.Register("subprocess", new SpinePlugin("subprocess", (ctx, _) => new Dsh.Subprocess.LocalSubprocessProvider(ctx)));
        catalog.Register("fs", new SpinePlugin("fs", (ctx, config) =>
        {
            var root = ConfigString(config, "root") ?? Environment.CurrentDirectory;
            var diffBasis = ConfigInt(config, "diffBasisMaxBytes");
            if (diffBasis is null)
            {
                var envDiffBasis = Environment.GetEnvironmentVariable("DSH_FS_DIFF_BASIS_MAX_BYTES");
                diffBasis = envDiffBasis is { Length: > 0 } && int.TryParse(envDiffBasis, out var parsed) ? parsed : null;
            }
            var service = new Dsh.Fs.LocalFileSystemProvider(ctx, new Dsh.Fs.FsProviderConfig(root, diffBasis ?? 10 * 1024 * 1024));
            var observations = new Dsh.Fs.FsObservations();
            ctx.Set("fsObservations", observations);
            var read = ctx.Tools().Register(Dsh.Fs.FileSystemTools.Read(service, observations: observations));
            var write = ctx.Tools().Register(Dsh.Fs.FileSystemTools.Write(service, observations));
            var edit = ctx.Tools().Register(Dsh.Fs.FileSystemTools.Edit(service, observations));
            var disposers = new List<IDisposable> { edit, write, read, service };
            if (ctx.Get<Dsh.Attachment.IAttachmentService>("attachment") is { } attachments)
            {
                disposers.Add(ctx.Tools().Register(Dsh.Fs.FileSystemTools.ReadImage(service, attachments, ctx.Llm())));
            }
            return new SpineDisposables(disposers.ToArray());
        }));
        catalog.Register("shell", new SpinePlugin("shell", (ctx, config) =>
        {
            var service = new Dsh.Shell.LocalShellProvider(ctx, new Dsh.Shell.ShellConfig
            {
                ShellPath = ConfigString(config, "shellPath") ?? Environment.GetEnvironmentVariable("DSH_SHELL_PATH") ?? (OperatingSystem.IsWindows() ? "cmd.exe" : "sh"),
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
        catalog.Register("runCode", new SpinePlugin("runCode", (ctx, _) =>
        {
            Dsh.Code.CodeEventTypes.Register();
            return new SpineDisposables(ctx.Tools().Register(Dsh.Code.RunCodeTool.Definition(ctx.Tools())));
        }));
        catalog.Register("cordis", new SpinePlugin("cordis", (ctx, _) =>
        {
            var runner = new Dsh.CordisRunner.DynamicCordisRunner();
            var disposables = new List<IDisposable>();
            foreach (var tool in Dsh.CordisRunner.CordisTools.Definitions(runner, ctx.Tools()))
            {
                disposables.Add(ctx.Tools().Register(tool));
            }
            return new SpineDisposables(disposables.ToArray());
        }));
        catalog.Register("web", new SpinePlugin("web", (ctx, _) =>
        {
            Dsh.Web.WebEventTypes.Register();
            var web = new Dsh.Web.WebRuntime(ctx);
            var disposers = new List<IDisposable> { web };
            // The corpus web-fetch fixture: the embedded loopback server answers the recorded
            // public.test authority through an address-pinned handler (node is not used in the
            // ported version); the default anonymous provider serves every other run.
            if (Environment.GetEnvironmentVariable("DSH_SNAPSHOT_WEB_FETCH") == "1")
            {
                var server = new Dsh.Web.FixtureWebFetchServer();
                disposers.Add(new CallbackDisposable(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult()));
                disposers.Add(web.RegisterFetchProvider(Dsh.Web.HttpWebProvider.WithAddressPin("public.test", 43117)));
            }
            else
            {
                disposers.Add(web.RegisterFetchProvider(new Dsh.Web.HttpWebProvider()));
            }
            disposers.Add(ctx.Tools().Register(Dsh.Web.WebTools.WebFetchDefinition(web)));
            // The DeepSeek search provider mounts when a base URL is configured (the corpus
            // channel is DSH_SNAPSHOT_WEB_SEARCH_BASE_URL; the recorded error fixture serves the
            // Messages endpoint on the recorded authority).
            var searchBase = Environment.GetEnvironmentVariable("DSH_SNAPSHOT_WEB_SEARCH_BASE_URL")
                ?? Environment.GetEnvironmentVariable("DEEPSEEK_SEARCH_BASE_URL");
            if (searchBase is not null)
            {
                if (Environment.GetEnvironmentVariable("DSH_SNAPSHOT_WEB_SEARCH_FIXTURE") == "1")
                {
                    var searchServer = new Dsh.Web.FixtureWebSearchServer();
                    disposers.Add(new CallbackDisposable(() => searchServer.DisposeAsync().AsTask().GetAwaiter().GetResult()));
                }
                var apiKey = Environment.GetEnvironmentVariable("DSH_SNAPSHOT_WEB_SEARCH_API_KEY")
                    ?? Environment.GetEnvironmentVariable("DEEPSEEK_SEARCH_API_KEY")
                    ?? string.Empty;
                disposers.Add(web.RegisterSearchProvider(new Dsh.Web.DeepSeekSearchProvider(apiKey, searchBase)));
            }
            disposers.Add(ctx.Tools().Register(Dsh.Web.WebTools.WebSearchDefinition(web)));
            return new SpineDisposables(disposers.ToArray());
        }));
        catalog.Register("credentials", new SpinePlugin("credentials", (ctx, _) => new Dsh.Credentials.LocalCredentialsProvider(ctx)));
        catalog.Register("settings", new SpinePlugin("settings", (ctx, config) =>
        {
            // The port is JSON-only (the TS provider also accepts YAML), so the default document
            // is settings.json under the harness home instead of settings.yaml.
            var dshHome = Environment.GetEnvironmentVariable("DSH_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            return new Dsh.Settings.FileSettingsProvider(ctx, ConfigString(config, "path") ?? Path.Combine(dshHome, "settings.json"));
        }));
        catalog.Register("authorization", new SpinePlugin("authorization", (ctx, _) =>
        {
            var credentials = ctx.Get<Dsh.Credentials.ICredentialsService>("credentials")
                ?? throw new InvalidOperationException("authorization requires the \"credentials\" row");
            return new Dsh.Authorization.LocalAuthorizationService(ctx, credentials);
        }));
        catalog.Register("sandbox", new SpinePlugin("sandbox", (ctx, config) =>
        {
            var backend = ConfigString(config, "backend") ?? "unsandboxed";
            return backend switch
            {
                "unsandboxed" => new Dsh.Sandbox.UnsandboxedSandboxProvider(ctx, new Dsh.Sandbox.SandboxConfig(
                    ConfigString(config, "workspaceRoot"))),
                "landlock" => new Dsh.Sandbox.LandlockSidecarSandboxProvider(ctx, new Dsh.Sandbox.LandlockSidecarConfig(
                    ConfigString(config, "sidecarPath"),
                    ConfigString(config, "workspaceRoot"))),
                _ => throw new InvalidOperationException($"sandbox: unknown backend \"{backend}\" (registered: unsandboxed, landlock)"),
            };
        }));
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
        catalog.Register("workspaceRegistry", new SpinePlugin("workspaceRegistry", (ctx, _) =>
        {
            var storage = ctx.Get<Dsh.Storage.IStorageService>("storage")
                ?? throw new InvalidOperationException("workspaceRegistry requires the \"storage\" row");
            var sessions = ctx.Get<Dsh.Session.SessionStore>("sessions");
            return new Dsh.Workspace.WorkspaceRegistry(ctx, storage,
                sessionKnown: sessions is null ? null : id => sessions.Get(id) is not null);
        }));
        catalog.Register("spill", new SpinePlugin("spill", (ctx, config) =>
        {
            var root = ConfigString(config, "root")
                ?? Environment.GetEnvironmentVariable("DSH_SNAPSHOT_SPILL_ROOT")
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
        catalog.Register("compaction", new SpinePlugin("compaction", (ctx, config) =>
            new Dsh.Compaction.BasicCompactionProvider(
                ctx,
                maxTokens: ConfigInt(config, "maxTokens")
                    ?? EnvInt("DSH_SNAPSHOT_COMPACTION_MAX_TOKENS")
                    ?? (int)Dsh.Compaction.CompactionPolicyDefaults.MaxTokens,
                maxOverflowRetries: ConfigInt(config, "maxOverflowRetries")
                    ?? EnvInt("DSH_SNAPSHOT_COMPACTION_MAX_OVERFLOW_RETRIES")
                    ?? Dsh.Compaction.BasicCompactionProvider.DefaultMaxOverflowRetries,
                auto: ConfigBool(config, "auto")
                    ?? EnvBool("DSH_SNAPSHOT_COMPACTION_AUTO")
                    ?? true)));
        catalog.Register("context", new SpinePlugin("context", (ctx, _) => new Dsh.Context.LocalContextProvider(ctx)));
        catalog.Register("sessionQuery", new SpinePlugin("sessionQuery", (ctx, _) => new Dsh.SessionQuery.LogSessionQueryProvider(ctx)));
        catalog.Register("preset", new SpinePlugin("preset", (ctx, config) =>
        {
            var root = Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "presets");
            var trust = ConfigString(config, "trust") switch
            {
                null or "user" => Dsh.Preset.PresetTrust.User,
                "system" => Dsh.Preset.PresetTrust.System,
                var value => throw new InvalidOperationException($"preset trust must be \"user\" or \"system\", got \"{value}\""),
            };
            var provider = new Dsh.Preset.FilePresetProvider(root, trust: trust);
            ctx.Set("preset", provider);
            return null;
        }));
        catalog.Register("workspaceInstructions", new SpinePlugin("workspaceInstructions", (ctx, _) =>
        {
            // The baseline message carries the root instruction files; a step's fs observations
            // queue a refresh that the next pre-step drains: newly discovered instruction files
            // under observed directories are prepended into the next-step inbox, removed again as
            // canceled, and folded into the entered batch (the recorded agent-instructions flow).
            var observations = ctx.Get<Dsh.Fs.FsObservations>("fsObservations");
            var messaged = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var refreshQueued = new HashSet<string>(StringComparer.Ordinal);
            var disposers = new List<IDisposable>();
            if (observations is not null)
            {
                disposers.Add(ctx.On("session/event",
                    new Action<Dsh.Session.Session, Dsh.Session.SessionEvent>((session, evt) =>
                    {
                        if (evt is Dsh.Session.StepEndEvent) refreshQueued.Add(session.Id.Value);
                    })));
            }
            disposers.Add(ctx.On("agent/pre-step",
                new Func<Dsh.AgentLoop.PreStepProposal, Func<Task<Dsh.AgentLoop.PreStepDecision>>, Task<Dsh.AgentLoop.PreStepDecision>>(async (proposal, next) =>
                {
                    var downstream = await next();
                    if (downstream is not Dsh.AgentLoop.EnterDecision enter) return downstream;
                    var session = proposal.Agent.Session;
                    var cwd = Environment.CurrentDirectory;
                    if (!messaged.TryGetValue(session.Id.Value, out var known))
                    {
                        known = new HashSet<string>(StringComparer.Ordinal);
                        messaged[session.Id.Value] = known;
                    }
                    // The refresh (queued at the prior step/end) discovers instruction files under
                    // the step's observed directories and delivers them as an additional message.
                    if (observations is not null && refreshQueued.Remove(session.Id.Value))
                    {
                        var desired = RefreshMessage(session, observations, cwd, known);
                        if (desired is not null)
                        {
                            proposal.Agent.Inbox.Prepend(Dsh.Agent.InboxTarget.NextStep, desired);
                            foreach (var pending in proposal.Agent.Inbox.NextStep.ToArray())
                            {
                                proposal.Agent.Inbox.Remove(pending.Id);
                            }
                            var refreshed = enter.Messages.ToList();
                            var insertAt = refreshed.Count - 1;
                            if (insertAt < 0) insertAt = 0;
                            refreshed.Insert(insertAt, desired);
                            return new Dsh.AgentLoop.EnterDecision(refreshed, enter.Assembly);
                        }
                    }
                    // The baseline message carries the root instruction files.
                    var candidate = new[] { "AGENTS.md", "CLAUDE.md" }
                        .Select(name => Path.Combine(cwd, name))
                        .FirstOrDefault(File.Exists);
                    if (candidate is null || known.Contains(Path.GetFileName(candidate))) return downstream;
                    var message = BuildWorkspaceInstructionsMessage(Path.GetFileName(candidate), File.ReadAllText(candidate), cwd);
                    known.Add(Path.GetFileName(candidate));
                    // The runtime-context message is the last entered message; the instructions
                    // land between the claimed batch and it (the recorded order).
                    var messages = enter.Messages.ToList();
                    var baselineInsertAt = messages.Count - 1;
                    if (baselineInsertAt < 0) baselineInsertAt = 0;
                    messages.Insert(baselineInsertAt, message);
                    return new Dsh.AgentLoop.EnterDecision(messages, enter.Assembly);
                })));
            return new SpineDisposables(disposers.ToArray());
        }));
        catalog.Register("skill", new SpinePlugin("skill", (ctx, config) =>
        {
            var root = ConfigString(config, "root")
                ?? Path.Combine(Environment.CurrentDirectory, ".dsh", "skills");
            var registry = new Dsh.Skill.SkillRegistry(ctx);
            var provider = new Dsh.Skill.FileSystemSkillProvider(root);
            var providerRegistration = registry.RegisterProvider(provider);
            var tool = ctx.Tools().Register(Dsh.Skill.SkillTools.Definition(registry));
            var disposers = new List<IDisposable> { tool, providerRegistration, registry };
            if (Directory.Exists(root))
            {
                // The skill-catalog reminder: the first pre-step batch of each session carries the
                // model-invocable skills as one appended user message (the recorded corpus shape —
                // once per session, not per step).
                var catalogInjected = new HashSet<string>(StringComparer.Ordinal);
                disposers.Add(ctx.On("agent/pre-step",
                    new Func<Dsh.AgentLoop.PreStepProposal, Func<Task<Dsh.AgentLoop.PreStepDecision>>, Task<Dsh.AgentLoop.PreStepDecision>>(async (proposal, next) =>
                    {
                        var downstream = await next();
                        if (downstream is not Dsh.AgentLoop.EnterDecision enter) return downstream;
                        if (!catalogInjected.Add(proposal.Agent.Id.Value)) return downstream;
                        var summaries = await registry.ListAsync(new Dsh.Skill.SkillLookupOptions(Cwd: Environment.CurrentDirectory)).ConfigureAwait(false);
                        var entries = summaries
                            .Where(summary => summary.Invocation.ModelInvocable)
                            .OrderBy(summary => summary.Name, StringComparer.Ordinal)
                            .Select(summary => new Dsh.Llm.SkillCatalogEntry(summary.Name, summary.Description))
                            .ToArray();
                        if (entries.Length == 0) return downstream;
                        var lines = new List<string>
                        {
                            "<system-reminder>",
                            "A skill is a reusable set of task-specific instructions. The following skills are available in this session:",
                            "",
                            "<available_skills>",
                        };
                        lines.AddRange(entries.Select(entry => $"- `{entry.Name}`: {entry.Description}"));
                        lines.Add("</available_skills>");
                        lines.Add("");
                        lines.Add("If the user names a skill, or the task clearly matches a skill's description, call the `skill` tool with the exact skill name before taking task actions. Load all applicable skills, then follow their full instructions. This catalog contains summaries only; do not infer or follow a skill's instructions until it has been loaded.");
                        lines.Add("A user may also invoke a skill directly; its <skill_content> block then appears in this conversation. Follow it, and do not call the `skill` tool again for that skill.");
                        lines.Add("</system-reminder>");
                        var catalog = new Dsh.Llm.UserMessage
                        {
                            Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("D")),
                            Content = new Dsh.Llm.ContentBlock[] { new Dsh.Llm.TextBlock(string.Join("\n", lines)) },
                            Source = new Dsh.Llm.SkillCatalogSource { Form = "catalog", Entries = entries },
                        };
                        return new Dsh.AgentLoop.EnterDecision(enter.Messages.Append(catalog).ToArray(), enter.Assembly);
                    })));
            }
            return new SpineDisposables(disposers.ToArray());
        }));
        catalog.Register("guard", new SpinePlugin("guard", (ctx, _) =>
            new SpineDisposables(new Dsh.Guard.ToolTimeoutPolicy(ctx), new Dsh.Guard.RepeatToolReminderGuard(ctx))));
        catalog.Register("terminal", new SpinePlugin("terminal", (ctx, config) =>
        {
            var backend = ConfigString(config, "backend") ?? "local";
            Dsh.Terminal.ITerminalService service = backend switch
            {
                "local" => new Dsh.Terminal.LocalTerminalProvider(ctx),
                "conpty" => new Dsh.Terminal.ConPtyTerminalProvider(ctx, new Dsh.Terminal.ConPtyConfig(
                    ConfigString(config, "shellPath"),
                    ConfigInt(config, "cols") ?? 160,
                    ConfigInt(config, "rows") ?? 40,
                    ConfigInt(config, "timeoutMs") ?? 30000,
                    ConfigInt(config, "idleSilenceMs") ?? 3000,
                    ConfigInt(config, "scrollbackLines") ?? 500,
                    ConfigString(config, "cwd"))),
                _ => throw new InvalidOperationException($"terminal: unknown backend \"{backend}\" (registered: local, conpty)"),
            };
            var tools = Dsh.Terminal.TerminalTools.Definitions(ctx);
            var disposers = tools.Select(ctx.Tools().Register).ToArray();
            return new SpineDisposables(disposers.Append((IDisposable)service).ToArray());
        }));
        catalog.Register("subagent", new SpinePlugin("subagent", (ctx, _) =>
        {
            var loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
                ?? throw new InvalidOperationException("subagent requires the \"agentLoop\" row");
            // The in-process driver runs each delegation as a real child agent: a fresh child
            // session under the parent's ancestry, one loop turn, and the child's final text as
            // the delegation result. The recorded child scripts bind to the child session through
            // the replay provider's first-call ordering.
            return new Dsh.Subagent.InProcessSubagentProvider(ctx, async (request, ct) =>
            {
                // The delegation-depth ceiling (the TS subagent default): a deeper call refuses
                // before any child session exists, with the recorded refusal wording.
                const int maxDelegationDepth = 2;
                var depth = (request.ParentDelegationDepth ?? 0) + 1;
                if (depth > maxDelegationDepth)
                {
                    throw new InvalidOperationException($"subagent depth {depth} exceeds maxDepth {maxDelegationDepth}");
                }
                var sessionId = new Dsh.Session.SessionId(Guid.NewGuid().ToString("D"));
                var options = new Dsh.Agent.AgentOptions
                {
                    Provider = request.Provider,
                    Model = request.Model,
                    Cwd = Environment.CurrentDirectory,
                    DelegationDepth = (request.ParentDelegationDepth ?? 0) + 1,
                    ParentSessionId = request.ParentSessionId,
                    Origin = "subagent",
                };
                var handle = loop.Create(sessionId, options, source: "subagent");
                try
                {
                    var driver = loop.GetLoop(sessionId)
                        ?? throw new InvalidOperationException("subagent: the child loop was not published");
                    var message = new Dsh.Llm.UserMessage
                    {
                        Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("D")),
                        Content = new Dsh.Llm.ContentBlock[] { new Dsh.Llm.TextBlock(request.Task) },
                        Source = new Dsh.Llm.UserSource(),
                    };
                    driver.Send(message, Dsh.Agent.InboxTarget.NextTurn, wakeup: true);
                    await driver.WhenIdleAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var session = handle.Agent.Session;
                    var text = string.Concat(session.Events
                        .OfType<Dsh.Session.AssistantMessageEvent>()
                        .SelectMany(evt => evt.Message.Content.OfType<Dsh.Llm.TextBlock>())
                        .Select(block => block.Text));
                    var reason = session.Events.OfType<Dsh.Session.TurnEndEvent>().LastOrDefault()?.Reason;
                    return new Dsh.Subagent.SubagentResult(
                        text,
                        Diagnostic: reason is Dsh.Session.ErrorReason error ? error.Failure.Message : null,
                        StopReason: reason switch
                        {
                            Dsh.Session.MaxTokensReason => Dsh.Subagent.SubagentStopReason.MaxTokens,
                            Dsh.Session.ErrorReason => Dsh.Subagent.SubagentStopReason.Error,
                            Dsh.Session.AbortedReason => Dsh.Subagent.SubagentStopReason.Aborted,
                            Dsh.Session.BlockedReason => Dsh.Subagent.SubagentStopReason.Refusal,
                            _ => Dsh.Subagent.SubagentStopReason.Completed,
                        });
                }
                finally
                {
                    handle.Dispose();
                }
            });
        }));
        catalog.Register("ralph", new SpinePlugin("ralph", (ctx, _) =>
        {
            var loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
                ?? throw new InvalidOperationException("ralph requires the \"agentLoop\" row");
            Dsh.Subagent.SubagentEventTypes.Register();
            return new SpineDisposables(new IDisposable[]
            {
                ctx.Tools().Register(Dsh.Workflow.RalphTool.StructuredOutputDefinition()),
                ctx.Tools().Register(Dsh.Workflow.RalphTool.Definition(loop)),
                Dsh.Workflow.RalphTool.InstallDescriptorListener(ctx),
            });
        }));
        catalog.Register("workflowTool", new SpinePlugin("workflowTool", (ctx, _) =>
        {
            // The script-based workflow tool mounts with the workflow row; this row exists so a
            // composition can mount the tool without the engine (unused by the base bundle).
            var loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
                ?? throw new InvalidOperationException("workflowTool requires the \"agentLoop\" row");
            Dsh.Workflow.WorkflowEventTypes.Register();
            return new SpineDisposables(ctx.Tools().Register(Dsh.Workflow.WorkflowTool.Definition(loop)));
        }));
        catalog.Register("lsp", new SpinePlugin("lsp", (ctx, _) =>
        {
            // The provider mounts only when a server configuration is present (the corpus
            // channel is DSH_SNAPSHOT_LSP_CONFIG; shipped profiles without one register nothing).
            var configJson = Environment.GetEnvironmentVariable("DSH_SNAPSHOT_LSP_CONFIG");
            if (string.IsNullOrWhiteSpace(configJson)) return null;
            var config = ParseLspConfig(configJson);
            var service = new Dsh.Lsp.LspService(ctx);
            IDisposable tool;
            IDisposable providerDisposal;
            if (config.Kind == "fixture")
            {
                // The recorded corpus runs an embedded fixture server (node is not used in the
                // ported version): no process is spawned.
                var provider = new Dsh.Lsp.FixtureLspProvider(
                    new Dsh.Lsp.LspProviderId(config.ProviderId), config.ExtensionToLanguage);
                var registration = service.RegisterProvider(provider);
                tool = ctx.Tools().Register(Dsh.Lsp.ToolLsp.Definition(service, config.MaxLocations));
                providerDisposal = new CallbackDisposable(registration);
                return new SpineDisposables(new IDisposable[] { tool, providerDisposal, service });
            }
            var cwd = Environment.CurrentDirectory;
            var spec = new Dsh.Lsp.LspInstanceSpec(
                config.Command, config.Args, cwd,
                Env: new Dictionary<string, string>(),
                MaxMessageBytes: 4 * 1024 * 1024,
                MaxStderrBytes: 64 * 1024,
                KillGraceMs: 1000,
                Configuration: null,
                WorkspaceUri: new Uri(cwd + Path.DirectorySeparatorChar).AbsoluteUri,
                InitializationOptions: null,
                ShutdownTimeoutMs: 1000);
            var stdioProvider = new Dsh.Lsp.StdioLspProvider(new Dsh.Lsp.LspProviderId(config.ProviderId), spec, config.ExtensionToLanguage);
            var stdioRegistration = service.RegisterProvider(stdioProvider);
            tool = ctx.Tools().Register(Dsh.Lsp.ToolLsp.Definition(service, config.MaxLocations));
            providerDisposal = new CallbackDisposable(stdioRegistration);
            return new SpineDisposables(new IDisposable[]
            {
                tool,
                providerDisposal,
                new CallbackDisposable(() => stdioProvider.DisposeAsync().AsTask().GetAwaiter().GetResult()),
                service,
            });
        }));
        catalog.Register("sdkSubagent", new SpinePlugin("sdkSubagent", (ctx, config) =>
        {
            var service = ctx.Get<Dsh.Subagent.ISubagentService>("subagent")
                ?? throw new InvalidOperationException("sdkSubagent requires the \"subagent\" row");
            var dshBin = ConfigString(config, "dshBin")
                ?? throw new InvalidOperationException("sdkSubagent requires a \"dshBin\" config pointing at the SDK runtime entry");
            var provider = new Dsh.Subagent.SdkOutOfProcessProvider(new Dsh.Subagent.SdkOutOfProcessConfig(
                dshBin,
                ConfigString(config, "profile") ?? "sdk",
                ConfigStrings(config, "patches"),
                ConfigString(config, "dshHome") ?? Path.Combine(ctx.Get<string>("dshProfileDir") ?? ".", "subagent-home"),
                ConfigString(config, "cwd"),
                ConfigString(config, "provider") ?? "deepseek-official",
                ConfigString(config, "model") ?? "deepseek-v4-flash",
                ConfigInt(config, "maxTokens"),
                ConfigMap(config, "env"),
                Array.Empty<string>(),
                ConfigInt(config, "shutdownTimeoutMs") ?? 1000,
                ConfigInt(config, "disposeEofGraceMs") ?? 6000,
                ConfigInt(config, "disposeGraceMs") ?? 3000));
            return service.RegisterProvider(provider);
        }));
        catalog.Register("subagentTool", new SpinePlugin("subagentTool", (ctx, config) =>
        {
            var service = ctx.Get<Dsh.Subagent.ISubagentService>("subagent")
                ?? throw new InvalidOperationException("subagentTool requires the \"subagent\" row");
            var providerName = ConfigString(config, "provider")
                ?? throw new InvalidOperationException("subagentTool requires a \"provider\" config naming the registered driver");
            return ctx.Tools().Register(Dsh.Subagent.SubagentTool.Definition(service, providerName, ConfigString(config, "toolName")));
        }));
        catalog.Register("jobs", new SpinePlugin("jobs", (ctx, _) =>
        {
            var service = new Dsh.Jobs.LocalJobsProvider(ctx);
            var output = ctx.Tools().Register(Dsh.Jobs.JobTools.JobOutputDefinition(ctx));
            var list = ctx.Tools().Register(Dsh.Jobs.JobTools.JobListDefinition(ctx));
            var kill = ctx.Tools().Register(Dsh.Jobs.JobTools.JobKillDefinition(ctx));
            return new SpineDisposables(kill, list, output, service);
        }));
        catalog.Register("workflow", new SpinePlugin("workflow", (ctx, _) =>
        {
            var loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
                ?? throw new InvalidOperationException("workflow requires the \"agentLoop\" row");
            var service = new Dsh.Workflow.WorkerThreadWorkflowProvider(ctx);
            Dsh.Workflow.WorkflowEventTypes.Register();
            var registration = ctx.Tools().Register(Dsh.Workflow.WorkflowTool.Definition(loop));
            return new SpineDisposables(registration, service);
        }));
        catalog.Register("webhook", new SpinePlugin("webhook", (ctx, _) => new Dsh.Webhook.WebhookRuntime(ctx)));
        catalog.Register("webhookIngress", new SpinePlugin("webhookIngress", (ctx, config) =>
        {
            var webhook = ctx.Get<Dsh.Webhook.IWebhookService>("webhook")
                ?? throw new InvalidOperationException("webhookIngress requires the \"webhook\" row");
            var credentials = ctx.Get<Dsh.Credentials.ICredentialsService>("credentials")
                ?? throw new InvalidOperationException("webhookIngress requires the \"credentials\" row");
            var prefix = ConfigString(config, "prefix")
                ?? throw new InvalidOperationException("webhookIngress requires a \"prefix\" config (for example http://127.0.0.1:8080/webhook/)");
            var secretRef = ConfigString(config, "secretRef")
                ?? throw new InvalidOperationException("webhookIngress requires a \"secretRef\" credential reference");
            var source = ConfigString(config, "source") ?? "primary-github";
            var maxBodyBytes = ConfigInt(config, "maxBodyBytes") ?? 1024 * 1024;
            var handler = new Dsh.Webhook.GitHubWebhookHandler(
                ctx, webhook, credentials,
                new Dsh.Webhook.GitHubWebhookHandlerConfig(
                    new Dsh.Webhook.WebhookSourceId(source), secretRef, maxBodyBytes));
            var ingress = new Dsh.Webhook.HttpListenerWebhookIngress(
                ctx, new Dsh.Webhook.HttpListenerWebhookIngressConfig(prefix, handler.HandleAsync, maxBodyBytes));
            // The ingress listens from mount time: a profile that names the row wants the
            // listener up before the first delivery, and a bound prefix fails the boot loud.
            ingress.StartAsync().GetAwaiter().GetResult();
            return new SpineDisposables(ingress);
        }));
        catalog.Register("tui", new SpinePlugin("tui", (ctx, _) =>
        {
            var args = ctx.Get<CmdlineArgs>("cmdlineArgs") ?? new CmdlineArgs(Array.Empty<string>());
            var code = Dsh.Tui.TuiApp.Run(args.Args.ToArray());
            var exit = ctx.Get<AppExit>("appExit")
                ?? throw new InvalidOperationException("dsh: tui requires the appExit launcher fact");
            exit.Exit(code);
            return null;
        }));
        catalog.Register("rpc", new SpinePlugin("rpc", (ctx, _) =>
        {
            var registry = new Dsh.Web.Host.DshRpcRegistry(ctx);
            var sessions = ctx.Get<Dsh.Session.SessionStore>("sessions")
                ?? throw new InvalidOperationException("rpc requires the \"sessions\" row");
            var loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
                ?? throw new InvalidOperationException("rpc requires the \"agentLoop\" row");
            var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            var provider = string.IsNullOrEmpty(key) ? "mock" : "deepseek";
            var model = string.IsNullOrEmpty(key) ? "mock-todo" : "deepseek-chat";
            var list = registry.Register(new Dsh.Web.Host.RpcMethod("session/list", (_, _) =>
            {
                var items = sessions.List().Select(session => new
                {
                    sessionId = session.Id.Value,
                    updatedAt = session.Events.LastOrDefault()?.TimeMs ?? 0,
                    running = false,
                    blank = !session.Events.OfType<Dsh.Session.AssistantMessageEvent>().Any(),
                    summary = session.Events.OfType<Dsh.Session.AssistantMessageEvent>()
                        .LastOrDefault()
                        ?.Message.Content.OfType<Dsh.Llm.TextBlock>().Select(block => block.Text).FirstOrDefault() ?? "",
                });
                return Task.FromResult<System.Text.Json.JsonElement?>(System.Text.Json.JsonSerializer.SerializeToElement(new { items }));
            }));
            var create = registry.Register(new Dsh.Web.Host.RpcMethod("session/create", (_, _) =>
            {
                // The loop owns the session identity: Create publishes the session itself.
                var id = new Dsh.Session.SessionId($"session-{Guid.NewGuid():N}");
                _ = loop.Create(id, new Dsh.Agent.AgentOptions { Provider = provider, Model = model });
                return Task.FromResult<System.Text.Json.JsonElement?>(
                    System.Text.Json.JsonSerializer.SerializeToElement(new { sessionId = id.Value }));
            }));
            var page = registry.Register(Dsh.Web.Host.SessionRemotes.Page(ctx, sessions));
            var follow = registry.RegisterStream(Dsh.Web.Host.SessionRemotes.Follow(ctx, sessions));
            var agents = ctx.Get<Dsh.Agent.AgentRegistry>("agents")
                ?? throw new InvalidOperationException("rpc requires the \"agents\" row");
            var jobs = ctx.Get<Dsh.Jobs.IJobsService>("jobs");
            var projections = ctx.Get<Dsh.Session.Projection.SessionProjectionRegistry>("sessionProjections");
            var control = registry.RegisterStream(Dsh.Web.Host.SessionControlRemotes.Control(ctx, sessions, agents, jobs, projections));
            // Settings, credentials, and workspace namespaces resolve their providers at invoke
            // time (the TS controllers keep the namespaces registered when a provider is absent).
            var settingsDescribe = registry.Register(Dsh.Web.Host.SettingsRemotes.Describe(ctx));
            var settingsUpdate = registry.Register(Dsh.Web.Host.SettingsRemotes.Update(ctx));
            var settingsReplace = registry.Register(Dsh.Web.Host.SettingsRemotes.Replace(ctx));
            var settingsMutate = registry.Register(Dsh.Web.Host.SettingsRemotes.Mutate(ctx));
            var settingsCanOpenPresetDir = registry.Register(Dsh.Web.Host.SettingsRemotes.CanOpenAgentPresetDirectory(ctx));
            var settingsOpenDocument = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenSettingsDocument(ctx));
            var settingsOpenPresetDir = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenAgentPresetDirectory(ctx));
            var credentialsDescribe = registry.Register(Dsh.Web.Host.CredentialsRemotes.Describe(ctx));
            var credentialsSet = registry.Register(Dsh.Web.Host.CredentialsRemotes.Set(ctx));
            var credentialsUnset = registry.Register(Dsh.Web.Host.CredentialsRemotes.Unset(ctx));
            var workspaceCreate = registry.Register(Dsh.Web.Host.WorkspaceRemotes.Create(ctx));
            var workspaceRename = registry.Register(Dsh.Web.Host.WorkspaceRemotes.Rename(ctx));
            var workspaceDelete = registry.Register(Dsh.Web.Host.WorkspaceRemotes.Delete(ctx));
            var workspaceInsertBefore = registry.Register(Dsh.Web.Host.WorkspaceRemotes.InsertBefore(ctx));
            var workspaceInsertSessionBefore = registry.Register(Dsh.Web.Host.WorkspaceRemotes.InsertSessionBefore(ctx));
            var workspaceArchiveSession = registry.Register(Dsh.Web.Host.WorkspaceRemotes.ArchiveSession(ctx));
            var workspaceFollow = registry.RegisterStream(Dsh.Web.Host.WorkspaceRemotes.Follow(ctx));
            var directoryPickerPick = registry.Register(Dsh.Web.Host.DirectoryPickerRemotes.Pick(ctx));
            var directoryPickerList = registry.Register(Dsh.Web.Host.DirectoryPickerRemotes.List(ctx));
            var directoryPickerCreate = registry.Register(Dsh.Web.Host.DirectoryPickerRemotes.CreateDirectory(ctx));
            var prompt = registry.Register(new Dsh.Web.Host.RpcMethod("session/prompt", async (args, ct) =>
            {
                var id = args is System.Text.Json.JsonElement element
                    && element.TryGetProperty("sessionId", out var sessionId)
                    && sessionId.ValueKind == System.Text.Json.JsonValueKind.String
                        ? sessionId.GetString()
                        : null;
                var text = args is System.Text.Json.JsonElement argsElement
                    && argsElement.TryGetProperty("text", out var textValue)
                    && textValue.ValueKind == System.Text.Json.JsonValueKind.String
                        ? textValue.GetString()
                        : null;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text))
                {
                    throw new Dsh.Web.Host.RpcBadRequestException("session/prompt requires sessionId and text");
                }
                var session = sessions.Get(new Dsh.Session.SessionId(id))
                    ?? throw new InvalidOperationException($"session \"{id}\" is not live");
                var driver = loop.GetLoop(session.Id)
                    ?? throw new InvalidOperationException($"session \"{id}\" has no live loop");
                var message = new Dsh.Llm.UserMessage
                {
                    Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("D")),
                    Content = new Dsh.Llm.ContentBlock[] { new Dsh.Llm.TextBlock(text) },
                    Source = new Dsh.Llm.UserSource(),
                };
                driver.Send(message, Dsh.Agent.InboxTarget.NextTurn, wakeup: true);
                await driver.WhenIdleAsync();
                var last = session.Events.OfType<Dsh.Session.AssistantMessageEvent>().LastOrDefault();
                var answer = last?.Message.Content.OfType<Dsh.Llm.TextBlock>().Select(block => block.Text).FirstOrDefault() ?? "";
                return System.Text.Json.JsonSerializer.SerializeToElement(new { sessionId = id, text = answer });
            }));
            return new SpineDisposables(
                follow, control, page, prompt, create, list,
                settingsDescribe, settingsUpdate, settingsReplace, settingsMutate,
                settingsCanOpenPresetDir, settingsOpenDocument, settingsOpenPresetDir,
                credentialsDescribe, credentialsSet, credentialsUnset,
                workspaceCreate, workspaceRename, workspaceDelete,
                workspaceInsertBefore, workspaceInsertSessionBefore, workspaceArchiveSession,
                workspaceFollow,
                directoryPickerPick, directoryPickerList, directoryPickerCreate);
        }));
        catalog.Register("webCore", new SpinePlugin("webCore", (ctx, _) =>
        {
            // The remote waterfall settlement: the $events stream registers pending proposals and
            // the $events/result unary settles them (the interaction surface over the web).
            var settlement = new Dsh.Web.Host.RemoteEventSettlement();
            ctx.Set("remoteEventSettlement", settlement);
            var rpc = ctx.Get<Dsh.Web.Host.DshRpcRegistry>("rpc");
            if (rpc is not null)
            {
                rpc.Register(Dsh.Web.Host.RemoteEventSettlement.ResultMethod(settlement));
            }
            // The shell's slot registry and page assembly registry are created here so the ui-*
            // rows register their contributions into the same instances the shell renders and the
            // webHost row maps. The webHost row must run AFTER the ui-* rows: the endpoint-level
            // route table needs every page assembly at map time (the SSR router matches through
            // the endpoint's route data), so the bundle orders webCore first, the ui-* rows next,
            // and webHost last.
            var slots = new Dsh.Web.App.Slots.SlotRegistry();
            ctx.Set("slots", slots);
            var pageAssemblies = new Dsh.Web.App.PageAssemblyRegistry();
            ctx.Set("pageAssemblies", pageAssemblies);
            return null;
        }));
        catalog.Register("webHost", new SpinePlugin("webHost", (ctx, config) =>
        {
            var slots = ctx.Get<Dsh.Web.App.Slots.SlotRegistry>("slots");
            var pageAssemblies = ctx.Get<Dsh.Web.App.PageAssemblyRegistry>("pageAssemblies");
            var host = new Dsh.Web.Host.WebHostService(
                ctx,
                new Dsh.Web.Host.WebHostConfig(
                    ConfigString(config, "host") ?? "127.0.0.1",
                    ConfigInt(config, "port") ?? 3080,
                    TrustedHosts: ConfigStringList(config, "trustedHosts")),
                configure: builder => builder.Services.AddDshApp(slots, pageAssemblies),
                map: app => app.MapDshApp(pageAssemblies?.List()));
            // The web profile serves from mount time: a bound port fails the boot loud.
            host.StartAsync().GetAwaiter().GetResult();
            if (host.Fence is not null && host.ListenUrl is not null)
            {
                Console.WriteLine($"dsh web: {host.Fence.AuthenticatedUrl(host.ListenUrl)}");
            }
            return host;
        }));
        catalog.Register("sdkRuntime", new SpinePlugin("sdkRuntime", (ctx, _) =>
        {
            // The SDK runtime profile: one JSON-RPC server over console stdio. Stdout is the wire,
            // so nothing else may write to it; the process exits when the client closes stdin
            // (EOF) after the shutdown exchange.
            var transport = new Dsh.Sdk.Protocol.JsonRpcLineTransport(
                Console.OpenStandardInput(), Console.OpenStandardOutput());
            var server = new Dsh.Sdk.Server.SdkJsonRpcServer(ctx, transport);
            transport.Start();
            var exit = ctx.Get<AppExit>("appExit")
                ?? throw new InvalidOperationException("dsh: sdkRuntime requires the appExit launcher fact");
            _ = Task.Run(async () =>
            {
                await transport.InputEnded;
                exit.Exit(0);
            });
            // The client's shutdown request already disposes the server's sessions; the ctx
            // disposal path covers an EOF without shutdown (a dead client).
            return new SpineDisposables(new CallbackDisposable(() =>
                server.ShutdownAsync().GetAwaiter().GetResult()));
        }));
        catalog.Register("acpRuntime", new SpinePlugin("acpRuntime", (ctx, config) =>
        {
            // The ACP profile: the standard Agent Client Protocol over console stdio. Stdout is
            // the wire, so nothing else may write to it; the process exits when the client closes
            // stdin (EOF). The route follows DEEPSEEK_API_KEY like the headless/web rows
            // (keyless runs use the mock route); a snapshot run follows the recorded
            // DSH_SNAPSHOT_PROVIDER/MODEL instead.
            var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            var provider = ConfigString(config, "provider")
                ?? Dsh.Llm.Replay.SnapshotEnv.Provider
                ?? (string.IsNullOrEmpty(key) ? Dsh.Spike.MockLlmProvider.Provider : "deepseek");
            var model = ConfigString(config, "model")
                ?? Dsh.Llm.Replay.SnapshotEnv.Model
                ?? (string.IsNullOrEmpty(key) ? Dsh.Spike.MockLlmProvider.Model : "deepseek-chat");
            var transport = new Dsh.Sdk.Protocol.JsonRpcLineTransport(
                Console.OpenStandardInput(), Console.OpenStandardOutput());
            var server = new Dsh.Acp.AcpServer(ctx, transport, new Dsh.Acp.AcpServerConfig(
                provider, model, ConfigInt(config, "sessionListPageSize") ?? 100));
            transport.Start();
            var exit = ctx.Get<AppExit>("appExit")
                ?? throw new InvalidOperationException("dsh: acpRuntime requires the appExit launcher fact");
            _ = Task.Run(async () =>
            {
                await transport.InputEnded;
                exit.Exit(0);
            });
            // The client's session/close requests already dispose the server's sessions; the ctx
            // disposal path covers an EOF without shutdown (a dead client).
            return new SpineDisposables(new CallbackDisposable(() =>
                server.ShutdownAsync().GetAwaiter().GetResult()));
        }));
        catalog.Register("hooksClaudeCode", new SpinePlugin("hooksClaudeCode", (ctx, config) =>
        {
            var path = ConfigString(config, "configPath")
                ?? Environment.GetEnvironmentVariable("DSH_HOOKS_CC_CONFIG");
            if (path is null) return null; // no hooks configured: the row is a no-op
            return new Dsh.Hooks.ClaudeCodeBridge(ctx, new Dsh.Hooks.ClaudeCodeBridgeConfig(
                Path.GetFullPath(path),
                ConfigString(config, "pluginRoot"),
                ConfigString(config, "projectDir"),
                ConfigInt(config, "defaultTimeoutMs") ?? Dsh.Hooks.HookRunner.DefaultHookTimeoutMs,
                ConfigInt(config, "stderrSummaryMaxChars") ?? Dsh.Hooks.HookLog.DefaultStderrSummaryMaxChars));
        }));
        catalog.Register("hooksCodex", new SpinePlugin("hooksCodex", (ctx, config) =>
        {
            var path = ConfigString(config, "configPath")
                ?? Environment.GetEnvironmentVariable("DSH_HOOKS_CODEX_CONFIG");
            if (path is null) return null; // no hooks configured: the row is a no-op
            return new Dsh.Hooks.CodexBridge(ctx, new Dsh.Hooks.CodexBridgeConfig(
                Path.GetFullPath(path),
                ConfigString(config, "model"),
                ConfigInt(config, "defaultTimeoutMs") ?? Dsh.Hooks.HookRunner.DefaultHookTimeoutMs,
                ConfigInt(config, "stderrSummaryMaxChars") ?? Dsh.Hooks.HookLog.DefaultStderrSummaryMaxChars));
        }));
        catalog.Register("llmRetry", new SpinePlugin("llmRetry", (ctx, config) =>
        {
            // Provider-routed request retry (port of dsh-llm-retry): the recorded corpus used the
            // deterministic snapshot policy (2 retries, 1ms delays, no jitter) over the standard
            // transient codes; the loop's request-error waterfall then retries the step.
            var retryableCodes = new[] { "EMPTY_RESPONSE", "RATE_LIMIT", "SERVER", "TIMEOUT", "TRANSPORT" };
            var maxRetries = ConfigInt(config, "maxRetries") ?? 2;
            var delayMs = ConfigInt(config, "delayMs") ?? 1;
            var policyKey = System.Text.Json.JsonSerializer.Serialize(new object?[]
            {
                "normal", maxRetries, retryableCodes, delayMs, delayMs, 0,
            });
            Dsh.AgentLoop.LlmRetryEventTypes.Register();
            return ctx.On("agent/request-error",
                new Func<Dsh.AgentLoop.RequestErrorProposal, Func<Task<Dsh.AgentLoop.RequestErrorAction?>>, Task<Dsh.AgentLoop.RequestErrorAction?>>(async (proposal, next) =>
                {
                    if (!retryableCodes.Contains(proposal.Failure.Code)) return await next();
                    var session = proposal.Agent.Session;
                    var previous = session.Events.OfType<Dsh.AgentLoop.LlmRetryEvent>()
                        .Where(evt => evt.Provider == proposal.Provider && evt.PolicyKey == policyKey)
                        .ToArray();
                    if (previous.Length >= maxRetries) return await next();
                    var retry = previous.Length + 1;
                    var retryId = previous.Length > 0 ? previous[^1].RetryId : Guid.NewGuid().ToString("D");
                    session.Append(new Dsh.AgentLoop.LlmRetryEvent
                    {
                        RetryId = retryId,
                        Turn = proposal.Turn,
                        Step = proposal.Step,
                        Provider = proposal.Provider,
                        Mode = "normal",
                        PolicyKey = policyKey,
                        Retry = retry,
                        MaxRetries = maxRetries,
                        DelayMs = delayMs,
                        Failure = proposal.Failure,
                    });
                    await Task.Delay(delayMs, proposal.Agent.CancellationToken).ConfigureAwait(false);
                    session.Append(new Dsh.AgentLoop.LlmRetryStartedEvent
                    {
                        RetryId = retryId,
                        Turn = proposal.Turn,
                        Step = proposal.Step,
                        Retry = retry,
                    });
                    return Dsh.AgentLoop.RetryDecision.Instance;
                }));
        }));
        catalog.Register("headless", new SpinePlugin("headless", (ctx, config) =>
        {
            var run = new HeadlessRun();
            var task = run.ApplyAsync(ctx, config);
            return task.AsTask().GetAwaiter().GetResult();
        }));
    }

    internal static string? ConfigString(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is string text
            ? text
            : null;

    /// <summary>
    /// Parse the snapshot-run model-metadata env (a JSON map of model id to
    /// {contextWindow, defaultMaxTokens, defaultReasoningEffort, reasoningEfforts}) into the
    /// replay provider's capability table; absent or empty means no adapter defaults.
    /// </summary>
    private static IReadOnlyDictionary<string, Dsh.Llm.LlmModelMetadata>? ParseModelMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dsh.Llm.LlmModelMetadata>>(json, options);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"DSH_SNAPSHOT_MODEL_META is not valid model metadata: {error.Message}");
        }
    }

    private static bool? ConfigBool(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is bool boolean
            ? boolean
            : null;

    private static int? ConfigInt(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is long integer
            ? (int)integer
            : null;

    private static int? EnvInt(string name)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    /// <summary>
    /// The refresh message for newly discovered instruction files under a step's observed
    /// directories (the recorded "Additional instructions from:" form). Returns null when no new
    /// file is found; the first new file per refresh is delivered.
    /// </summary>
    private static Dsh.Llm.UserMessage? RefreshMessage(
        Dsh.Session.Session session, Dsh.Fs.FsObservations observations, string cwd, HashSet<string> known)
    {
        var candidates = new[] { "AGENTS.md", "CLAUDE.md", "AGENTS.local.md", "CLAUDE.local.md" };
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in observations.Targets(session.Id.Value))
        {
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(directory) || !scanned.Add(directory)) continue;
            foreach (var name in candidates)
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path) || known.Contains(path)) continue;
                var relative = Path.GetRelativePath(cwd, path).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
                var relativeDir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
                var content = File.ReadAllText(path);
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                known.Add(path);
                var text = "<system-reminder>\n"
                    + $"Additional instructions from: {relative}\n\n"
                    + $"These instructions apply to work under `{relativeDir}`. Use them as guidance when relevant; "
                    + "more specific instructions take precedence. They do not override system, developer, or direct user instructions.\n\n"
                    + $"{content.TrimEnd('\n', '\r')}\n\n</system-reminder>";
                return new Dsh.Llm.UserMessage
                {
                    Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("D")),
                    Content = new Dsh.Llm.ContentBlock[] { new Dsh.Llm.TextBlock(text) },
                    Source = new Dsh.Llm.AgentInstructionsSource
                    {
                        Form = "instructions",
                        Changes = new[]
                        {
                            new Dsh.Llm.InstructionChange("set", relativeDir + "\u0000" + name, relative, digest),
                        },
                    },
                };
            }
        }
        return null;
    }

    /// <summary>
    /// The workspace-instructions reminder: a system-reminder user message carrying the root
    /// AGENTS.md/CLAUDE.md content, the baseline identity, and the file changes (the recorded
    /// agent-instructions source shape).
    /// </summary>
    private static Dsh.Llm.UserMessage BuildWorkspaceInstructionsMessage(string fileName, string content, string cwd)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var baselineIdentity = "{\"projectRoot\":\"\",\"projectRootMarkers\":[\".git\"],\"maxBytes\":65536,\"maxSourceBytes\":1048576,"
            + "\"instructionFileCandidates\":[\"AGENTS.md\",\"CLAUDE.md\"],\"localInstructionFileCandidates\":[\"AGENTS.local.md\",\"CLAUDE.local.md\"]}";
        var text = "<system-reminder>\n"
            + "The following workspace instructions may be relevant to your work. Use them as guidance when applicable. "
            + "More specific instructions take precedence over broader ones. They do not override system, developer, or direct user instructions.\n\n"
            + $"Instructions from: {fileName}\n\n{content.TrimEnd('\n', '\r')}\n\n</system-reminder>";
        var relative = fileName;
        return new Dsh.Llm.UserMessage
        {
            Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("D")),
            Content = new Dsh.Llm.ContentBlock[] { new Dsh.Llm.TextBlock(text) },
            Source = new Dsh.Llm.AgentInstructionsSource
            {
                Form = "instructions",
                Baseline = true,
                BaselineIdentity = baselineIdentity,
                Changes = new[]
                {
                    new Dsh.Llm.InstructionChange("set", ".\u0000" + relative, relative, digest),
                },
            },
        };
    }

    private static bool? EnvBool(string name)
        => bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    /// <summary>The corpus LSP server configuration parsed from DSH_SNAPSHOT_LSP_CONFIG.</summary>
    private sealed record LspServerConfig(
        string ProviderId, string Kind, string Command, IReadOnlyList<string> Args,
        IReadOnlyDictionary<string, string> ExtensionToLanguage, int MaxLocations);

    private static LspServerConfig ParseLspConfig(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var command = root.TryGetProperty("command", out var commandValue) && commandValue.ValueKind == JsonValueKind.String
                ? commandValue.GetString() ?? string.Empty
                : string.Empty;
            var args = root.TryGetProperty("args", out var argsValue) && argsValue.ValueKind == JsonValueKind.Array
                ? argsValue.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                : Array.Empty<string>();
            var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("extensionToLanguage", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var pair in map.EnumerateObject()) extensions[pair.Name] = pair.Value.GetString() ?? string.Empty;
            }
            var maxLocations = root.TryGetProperty("maxLocations", out var max) && max.ValueKind == JsonValueKind.Number
                ? max.GetInt32()
                : Dsh.Lsp.LspRender.DefaultMaxLocations;
            var providerId = root.TryGetProperty("providerId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? "lsp"
                : "lsp";
            var kind = root.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String
                ? kindValue.GetString() ?? "stdio"
                : "stdio";
            if (kind == "stdio" && command.Length == 0)
            {
                throw new InvalidOperationException("DSH_SNAPSHOT_LSP_CONFIG: command must be a non-empty string");
            }
            return new LspServerConfig(providerId, kind, command, args, extensions, maxLocations);
        }
        catch (Exception error) when (error is not InvalidOperationException)
        {
            throw new InvalidOperationException($"DSH_SNAPSHOT_LSP_CONFIG is not valid server configuration: {error.Message}");
        }
    }

    private static IReadOnlyList<string> ConfigStrings(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is List<object?> list
            ? list.OfType<string>().ToArray()
            : Array.Empty<string>();

    /// <summary>Read a list-of-strings config value, failing loud on any non-string element (the TS zod array).</summary>
    private static IReadOnlyList<string> ConfigStringList(object? config, string key)
    {
        if (config is not Dictionary<string, object?> map || !map.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }
        if (value is not List<object?> list)
        {
            throw new InvalidOperationException($"config {key} must be a list of strings");
        }
        var result = new string[list.Count];
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] is not string text)
            {
                throw new InvalidOperationException($"config {key} entry {index} must be a string");
            }
            result[index] = text;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ConfigMap(object? config, string key)
        => config is Dictionary<string, object?> map && map.TryGetValue(key, out var value) && value is Dictionary<string, object?> entries
            ? entries
                .Where(entry => entry.Value is string)
                .ToDictionary(entry => entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal)
            : new Dictionary<string, string>();
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

/// <summary>An <see cref="IDisposable"/> running one callback (the spine rows' adapter for async services).</summary>
internal sealed class CallbackDisposable : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    public CallbackDisposable(Action dispose)
    {
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispose();
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

