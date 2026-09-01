using System.Text.Json;
using Cordis.Core;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Session.Persistence;
using Dsh.Shell;
using Dsh.Tools;

namespace Dsh.Hooks;

/// <summary>Plugin config for the Codex bridge: where the Codex hook config lives + the model name for payloads.</summary>
public sealed record CodexBridgeConfig(
    /// <summary>Path to a Codex <c>hooks.json</c>.</summary>
    string ConfigPath,
    /// <summary>The model name stamped on every payload (Codex includes <c>model</c> on each event).</summary>
    string? Model = null,
    /// <summary>Default per-hook timeout in ms when a hook sets none (Codex default: 600000).</summary>
    int DefaultTimeoutMs = HookRunner.DefaultHookTimeoutMs,
    /// <summary>Character cap for the <c>hook/result</c> event's persisted stderr summary.</summary>
    int StderrSummaryMaxChars = HookLog.DefaultStderrSummaryMaxChars);

/// <summary>
/// Bridge for unmodified Codex command hooks on harness interception points (port of
/// <c>@deepseek-ai/dsh-hooks-codex</c>). It supports five points (SessionStart, prompt/tool
/// pre/post, Stop), regex-only matchers, snake_case payloads without a trailing newline, no hook
/// environment or command substitution, and no pre-tool approval or rewrite path; only blocking
/// decisions are honored. Documented reductions: the ported session header carries no workspace
/// cwd (the payload cwd and hook workdir fall back to the process cwd), and the post-tool
/// <c>additionalContext</c> is injected into the next step (the port's tool decisions carry no
/// additional-context slots).
/// </summary>
public sealed class CodexBridge : IDisposable
{
    private const string PluginName = "hooks-codex";

    private readonly Context _ctx;
    private readonly CodexBridgeConfig _config;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<MatcherGroup>> _groups;
    private readonly DetachedRuns _detached = new();
    private readonly List<IDisposable> _disposers = new();
    private readonly IShellService _shell;
    private readonly Dsh.AgentLoop.AgentLoop? _loop;
    private readonly SessionPersistenceService? _persistence;
    private readonly Dictionary<(string Session, string CallId), UserMessage> _pendingContext = new();
    private int _handlerCounter;

    /// <summary>Create the bridge over one context: parse the config once, then register the extension-point listeners.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="config">the bridge configuration.</param>
    public CodexBridge(Context ctx, CodexBridgeConfig config)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.DefaultTimeoutMs <= 0 || config.StderrSummaryMaxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "defaultTimeoutMs and stderrSummaryMaxChars must be positive");
        }
        _shell = ctx.Get<IShellService>("shell")
            ?? throw new InvalidOperationException("hooks-codex requires the \"shell\" row");
        _loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop");
        _persistence = ctx.Get<SessionPersistenceService>("sessionPersistence");
        try
        {
            var parsed = CodexConfig.Parse(File.ReadAllText(config.ConfigPath));
            _groups = parsed.Config;
            foreach (var skipped in parsed.Skipped)
            {
                ctx.Logger.Warn($"hooks-codex: skipping {skipped.Reason} on {skipped.Event} (only sync command hooks run)");
            }
        }
        catch (Exception error)
        {
            ctx.Logger.Warn($"hooks-codex: could not load hook config \"{config.ConfigPath}\": {error.Message} — no hooks registered");
            _groups = new Dictionary<string, IReadOnlyList<MatcherGroup>>();
            return;
        }
        RegisterListeners();
    }

    /// <summary>Dispose the listeners and drain the detached hook runs to quiescence.</summary>
    public void Dispose()
    {
        foreach (var disposer in _disposers) disposer.Dispose();
        _disposers.Clear();
        _detached.DrainAsync().GetAwaiter().GetResult();
    }

    private void RegisterListeners()
    {
        // SessionStart is the one emit-shaped (detached) point Codex has: track its run chain so
        // disposal aborts a still-running hook process and drains the continuation. Clean plain
        // stdout becomes injected context.
        _disposers.Add(_ctx.On("agent/session-start", new Action<SessionStartPayload>(payload =>
        {
            var driver = _loop?.GetLoop(payload.Agent.Session.Id);
            _detached.Track(RunPointAsync("SessionStart", payload.Source, BuildSessionStartPayload(payload),
                payload.Agent.Session, null, _detached.Signal, plainStdoutAsContext: true)
                .ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                    {
                        _ctx.Logger.Warn($"hooks-codex: SessionStart hook failed: {completed.Exception?.GetBaseException().Message}");
                        return;
                    }
                    var context = ContextFrom(completed.Result);
                    if (context is not null && driver is not null) driver.Inject(context);
                }, TaskScheduler.Default));
        })));

        // UserPromptSubmit → PreStepDecision. Codex supports reject, not rewrite or ask.
        _disposers.Add(_ctx.On("agent/pre-step",
            new Func<PreStepProposal, Func<Task<PreStepDecision>>, Task<PreStepDecision>>(async (proposal, next) =>
            {
                if (proposal.Messages.Count == 0) return await next();
                var merged = await RunPointAsync("UserPromptSubmit", "", BuildPromptPayload(proposal),
                    proposal.Agent.Session, proposal.Turn, CancellationToken.None, plainStdoutAsContext: true);
                if (merged.Decision == "deny") return new RejectDecision();
                // Context alone is not a veto: DELEGATE so a later pre-step listener can still
                // reject/rewrite, then fold our context onto its decision.
                var downstream = await next();
                var ours = ContextFrom(merged);
                if (ours is null || downstream is not EnterDecision enter) return downstream;
                return new EnterDecision(enter.Messages.Append(ours).ToArray(), enter.Assembly);
            })));

        // PreToolUse → PreToolDecision. Codex blocks only (no allow/ask honored).
        _disposers.Add(_ctx.On("tools/pre-execute",
            new Func<ToolRunContext, Func<Task<PreToolDecision>>, Task<PreToolDecision>>(async (exec, next) =>
            {
                var merged = await RunPointAsync("PreToolUse", exec.Name, BuildPreToolPayload(exec), exec.Session,
                    exec.Session is null ? null : LastTurn(exec.Session), exec.CancellationToken, plainStdoutAsContext: false);
                if (merged.Decision == "deny") return new DenyDecision(merged.Reason ?? "blocked by PreToolUse hook");
                return await next();
            })));

        // PostToolUse → PostToolDecision (block with feedback, or attach context to the next step).
        _disposers.Add(_ctx.On("tools/post-execute",
            new Func<ToolRunContext, ToolExecutionResult, Func<Task<PostToolDecision>>, Task<PostToolDecision>>(async (exec, result, next) =>
            {
                var merged = await RunPointAsync("PostToolUse", exec.Name, BuildPostToolPayload(exec, result), exec.Session,
                    exec.Session is null ? null : LastTurn(exec.Session), exec.CancellationToken, plainStdoutAsContext: false);
                var context = ContextFrom(merged);
                if (merged.Decision == "deny")
                {
                    // The recorded corpus appends the durable tool/result BEFORE the injected
                    // context splice, so the delivery is deferred to the session event.
                    QueueNextStep(exec.Session, exec.CallId, context);
                    return new BlockDecision(new ContentBlock[] { new TextBlock(merged.Reason ?? "blocked by PostToolUse hook") });
                }
                QueueNextStep(exec.Session, exec.CallId, context);
                return await next();
            })));

        // Deliver post-tool contexts once the durable tool/result event commits, so the recorded
        // event order (result, then the next-step context splice) reproduces exactly.
        _disposers.Add(_ctx.On("session/event", new Action<Dsh.Session.Session, SessionEvent>((session, evt) =>
        {
            if (evt is not ToolResultEvent toolResult) return;
            var callId = (toolResult.Message.Source as ToolSource)?.CallId;
            if (callId is null) return;
            if (!_pendingContext.Remove((session.Id.Value, callId.Value), out var context)) return;
            _loop?.GetLoop(session.Id)?.Inject(context);
        })));

        // A blocking Stop hook steers at the stopping boundary (the TS's stop-loop-guard TODO
        // applies: an unconditionally blocking hook force-continues every step until it self-limits).
        _disposers.Add(_ctx.On("agent/turn-stopping", new Action<TurnStoppingProposal>(proposal =>
        {
            var merged = RunPointAsync("Stop", "", BuildStopPayload(proposal.Agent), proposal.Agent.Session, proposal.Turn,
                CancellationToken.None, plainStdoutAsContext: false).GetAwaiter().GetResult();
            if (merged.Decision == "deny")
            {
                var text = merged.Reason ?? "continue: blocked by Stop hook";
                _loop?.GetLoop(proposal.Agent.Session.Id)?.Steer(PluginMessage(text));
            }
        })));
    }

    /// <summary>Run and fold one configured Codex hook point; a supplied turn records the invoked/result pair inside that open turn.</summary>
    private async Task<MergedHookOutcome> RunPointAsync(string point, string matchQuery, object payload,
        Dsh.Session.Session? session, long? turn, CancellationToken signal, bool plainStdoutAsContext)
    {
        if (!_groups.TryGetValue(point, out var groups)) return NeutralOutcome();
        var outputs = new List<HookOutput>();
        foreach (var group in groups)
        {
            // Codex always interprets matchers as regexes; it has no literal fast path.
            if (!HookMatcher.Matches(group.Matcher, matchQuery, MatcherMode.Codex)) continue;
            foreach (var hook in group.Hooks)
            {
                var handlerId = NextHandlerId(point);
                if (session is not null && turn is not null)
                {
                    HookLog.AppendInvoked(session, turn.Value, point, HookDialect.Codex, handlerId, group.Matcher);
                }
                var result = HookRunner.RunHook(_shell, hook, new HookRunner.RunHookOptions(
                    payload, Env: null, Cwd: null, signal, TrailingNewline: false, _config.DefaultTimeoutMs, point));
                // Clean plain stdout becomes context only when no structured context exists;
                // nonzero output and raw JSON never leak as prose.
                if (plainStdoutAsContext && result.Output.ExitCode == 0
                    && result.Output.AdditionalContext is null
                    && result.Output.Stdout.Length > 0
                    && !result.Output.Stdout.StartsWith('{'))
                {
                    result = result with { Output = result.Output with { AdditionalContext = result.Output.Stdout } };
                }
                if (result.Output.SystemMessage is not null)
                {
                    _ctx.Logger.Warn($"hooks-codex: {point} hook emitted a systemMessage, which is not yet surfaced (ignored)");
                }
                outputs.Add(result.Output);
                if (session is not null && turn is not null)
                {
                    HookLog.AppendResult(session, turn.Value, point, handlerId, result.Output, _config.StderrSummaryMaxChars, result.DurationMs);
                }
            }
        }
        return HookMerge.MergeHookOutputs(outputs);
    }

    private string NextHandlerId(string point) => $"codex:{point}:{++_handlerCounter}";

    private static MergedHookOutcome NeutralOutcome() => new("none", null, false, null, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Build additional model context from hook output, or return null when empty.</summary>
    private UserMessage? ContextFrom(MergedHookOutcome merged)
    {
        if (merged.AdditionalContext.Count == 0) return null;
        return new UserMessage
        {
            Id = new MessageId(Guid.NewGuid().ToString("D")),
            Content = merged.AdditionalContext.Select(text => (ContentBlock)new TextBlock(text)).ToArray(),
            Source = new PluginSource { Plugin = PluginName },
        };
    }

    private UserMessage PluginMessage(string text) => new()
    {
        Id = new MessageId(Guid.NewGuid().ToString("D")),
        Content = new ContentBlock[] { new TextBlock(text) },
        Source = new PluginSource { Plugin = PluginName },
    };

    private void QueueNextStep(Dsh.Session.Session? session, ToolCallId callId, UserMessage? context)
    {
        if (context is null || session is null) return;
        _pendingContext[(session.Id.Value, callId.Value)] = context;
    }

    private void InjectNextStep(Dsh.Session.Session? session, UserMessage? context)
    {
        if (context is null || session is null) return;
        _loop?.GetLoop(session.Id)?.Inject(context);
    }

    private static long LastTurn(Dsh.Session.Session session)
        => session.Events.OfType<TurnStartEvent>().Select(evt => evt.Turn).DefaultIfEmpty(0).Last();

    private Dictionary<string, object?> Base(Dsh.Agent.Agent? agent, string eventName)
    {
        var session = agent?.Session;
        return new Dictionary<string, object?>
        {
            ["session_id"] = session?.Id.Value ?? "",
            ["transcript_path"] = session is null ? null : (_persistence?.LogPath(session.Id) ?? null),
            // The port's session header carries no workspace cwd (documented reduction).
            ["cwd"] = Environment.CurrentDirectory,
            ["hook_event_name"] = eventName,
            ["model"] = _config.Model ?? "",
            ["permission_mode"] = "default",
        };
    }

    private Dictionary<string, object?> TurnBase(Dsh.Agent.Agent? agent, string eventName)
        => new(Base(agent, eventName)) { ["turn_id"] = agent is null ? 0 : LastTurn(agent.Session) };

    private Dictionary<string, object?> BuildSessionStartPayload(SessionStartPayload payload)
        => new(Base(payload.Agent, "SessionStart")) { ["source"] = payload.Source };

    private Dictionary<string, object?> BuildPromptPayload(PreStepProposal proposal)
        => new(TurnBase(proposal.Agent, "UserPromptSubmit"))
        {
            ["prompt"] = BlocksToText(proposal.Messages.SelectMany(message => message.Content)),
        };

    private Dictionary<string, object?> BuildPreToolPayload(ToolRunContext exec)
        => new(TurnBase(exec.Session is null ? null : AgentOf(exec.Session), "PreToolUse"))
        {
            // tool_name is the REAL tool name (matching the exec.Name matcher subject); tool_input
            // keeps Codex's { command } shape, derived from the call's command arg when present.
            ["tool_name"] = exec.Name,
            ["tool_input"] = new Dictionary<string, object?> { ["command"] = CommandOf(exec.Arguments) },
            ["tool_use_id"] = exec.CallId.Value,
        };

    private Dictionary<string, object?> BuildPostToolPayload(ToolRunContext exec, ToolExecutionResult result)
        => new(TurnBase(exec.Session is null ? null : AgentOf(exec.Session), "PostToolUse"))
        {
            ["tool_name"] = exec.Name,
            ["tool_input"] = new Dictionary<string, object?> { ["command"] = CommandOf(exec.Arguments) },
            ["tool_use_id"] = exec.CallId.Value,
            ["tool_response"] = BlocksToText(result.Content),
        };

    private Dictionary<string, object?> BuildStopPayload(Dsh.Agent.Agent agent)
        => new(TurnBase(agent, "Stop"))
        {
            ["stop_hook_active"] = false,
            ["last_assistant_message"] = null,
        };

    private Dsh.Agent.Agent? AgentOf(Dsh.Session.Session session)
        => _ctx.Get<Dsh.Agent.AgentRegistry>("agents")?.List().FirstOrDefault(agent => ReferenceEquals(agent.Session, session));

    /// <summary>Extract a <c>command</c> string from a tool call's parsed arguments, else ''.</summary>
    private static string CommandOf(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String
                ? command.GetString()!
                : "";

    private static string BlocksToText(IEnumerable<ContentBlock> content)
        => string.Concat(content.OfType<TextBlock>().Select(block => block.Text));
}
