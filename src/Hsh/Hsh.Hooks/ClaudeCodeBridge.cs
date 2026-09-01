using Harness.Cordis.Core;
using Harness.Agent;
using Harness.AgentLoop;
using Harness.Llm;
using Harness.Session;
using Harness.Session.Persistence;
using Harness.Shell;
using Harness.Tools;

namespace Harness.Hooks;

/// <summary>Plugin config for the Claude Code bridge: where the CC hook config lives + substitution roots.</summary>
public sealed record ClaudeCodeBridgeConfig(
    /// <summary>Path to a <c>hooks.json</c> or a settings file whose <c>hooks</c> key holds the config.</summary>
    string ConfigPath,
    /// <summary>Replaces <c>${CLAUDE_PLUGIN_ROOT}</c> in command strings (the plugin's root dir).</summary>
    string? PluginRoot = null,
    /// <summary>Replaces <c>${CLAUDE_PROJECT_DIR}</c> in command strings AND is exported as the <c>CLAUDE_PROJECT_DIR</c> env var for hook processes.</summary>
    string? ProjectDir = null,
    /// <summary>Default per-hook timeout in ms when a hook sets none (CC default: 600000).</summary>
    int DefaultTimeoutMs = HookRunner.DefaultHookTimeoutMs,
    /// <summary>Character cap for the <c>hook/result</c> event's persisted stderr summary.</summary>
    int StderrSummaryMaxChars = HookLog.DefaultStderrSummaryMaxChars);

/// <summary>
/// Bridge for unmodified Claude Code command hooks on harness interception extension points
/// (port of <c>@deepseek-ai/hsh-hooks-claude-code</c>). It supports SessionStart, prompt/tool
/// pre/post, Stop, and subagent start/stop; it owns Claude payloads, environment, substitution,
/// and decision mapping. Documented reductions: the ported session header carries no workspace
/// cwd (the payload cwd and hook workdir fall back to the process cwd), SubagentStart/SubagentStop
/// parse but never fire (the port's subagent seam has no start/end lifecycle events), a
/// <c>permissionDecision: ask</c> maps to a deny (the port's pre-tool decisions have no ask seat),
/// and the post-tool <c>additionalContext</c> is injected into the next step (the port's tool
/// decisions carry no additional-context slots).
/// </summary>
public sealed class ClaudeCodeBridge : IDisposable
{
    private const string PluginName = "hooks-claude-code";

    private readonly Context _ctx;
    private readonly ClaudeCodeBridgeConfig _config;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<MatcherGroup>> _groups;
    private readonly DetachedRuns _detached = new();
    private readonly List<IDisposable> _disposers = new();
    private readonly IShellService _shell;
    private readonly Harness.AgentLoop.AgentLoop? _loop;
    private readonly SessionPersistenceService? _persistence;
    private readonly Dictionary<(string Session, string CallId), UserMessage> _pendingContext = new();
    private int _handlerCounter;

    /// <summary>Create the bridge over one context: parse the config once, then register the extension-point listeners.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="config">the bridge configuration.</param>
    public ClaudeCodeBridge(Context ctx, ClaudeCodeBridgeConfig config)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.DefaultTimeoutMs <= 0 || config.StderrSummaryMaxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "defaultTimeoutMs and stderrSummaryMaxChars must be positive");
        }
        _shell = ctx.Get<IShellService>("shell")
            ?? throw new InvalidOperationException("hooks-claude-code requires the \"shell\" row");
        _loop = ctx.Get<Harness.AgentLoop.AgentLoop>("agentLoop");
        _persistence = ctx.Get<SessionPersistenceService>("sessionPersistence");
        try
        {
            var parsed = ClaudeCodeConfig.Parse(File.ReadAllText(config.ConfigPath), config.PluginRoot, config.ProjectDir);
            _groups = parsed.Config;
            foreach (var skipped in parsed.Skipped)
            {
                ctx.Logger.Warn($"hooks-claude-code: skipping unsupported \"{skipped.Type}\" hook on {skipped.Event} (only command hooks run)");
            }
        }
        catch (Exception error)
        {
            // A read or parse failure logs and registers nothing.
            ctx.Logger.Warn($"hooks-claude-code: could not load hook config \"{config.ConfigPath}\": {error.Message} — no hooks registered");
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
        // SessionStart injects context when its detached hook resolves; a slow hook may miss the
        // first request (the TS's documented startup gate TODO).
        _disposers.Add(_ctx.On("agent/session-start", new Action<SessionStartPayload>(payload =>
        {
            var driver = _loop?.GetLoop(payload.Agent.Session.Id);
            _detached.Track(RunPointAsync("SessionStart", payload.Source, BuildSessionStartPayload(payload), payload.Agent.Session, null, _detached.Signal)
                .ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                    {
                        _ctx.Logger.Warn($"hooks-claude-code: SessionStart hook failed: {completed.Exception?.GetBaseException().Message}");
                        return;
                    }
                    var context = ContextFrom(completed.Result);
                    if (context is not null && driver is not null) driver.Inject(context);
                }, TaskScheduler.Default));
        })));

        // UserPromptSubmit → PreStepDecision. The prompt text is the payload; no matcher subject
        // (CC ignores matchers for this event).
        _disposers.Add(_ctx.On("agent/pre-step",
            new Func<PreStepProposal, Func<Task<PreStepDecision>>, Task<PreStepDecision>>(async (proposal, next) =>
            {
                if (proposal.Messages.Count == 0) return await next();
                var merged = await RunPointAsync("UserPromptSubmit", "", BuildPromptPayload(proposal), proposal.Agent.Session, proposal.Turn, CancellationToken.None);
                if (merged.Decision == "deny") return new RejectDecision();
                // Delegate so later listeners may still rewrite or reject, then prepend our
                // context only to a downstream enter decision.
                var downstream = await next();
                var ours = ContextFrom(merged);
                if (ours is null || downstream is not EnterDecision enter) return downstream;
                return new EnterDecision(enter.Messages.Append(ours).ToArray(), enter.Assembly);
            })));

        // PreToolUse → PreToolDecision. Matcher subject is the tool name.
        _disposers.Add(_ctx.On("tools/pre-execute",
            new Func<ToolRunContext, Func<Task<PreToolDecision>>, Task<PreToolDecision>>(async (exec, next) =>
            {
                var merged = await RunPointAsync("PreToolUse", exec.Name, BuildPreToolPayload(exec), exec.Session,
                    exec.Session is null ? null : LastTurn(exec.Session), exec.CancellationToken);
                if (merged.Decision == "deny") return new DenyDecision(merged.Reason ?? "blocked by PreToolUse hook");
                if (merged.Decision == "ask")
                {
                    // A hook ask records the durable approval/asked + approval/decided pair and
                    // rejects with the recorded wording (the snapshot runs have no answerer, so
                    // the ask always settles rejected — the recorded corpus shape).
                    if (exec.Session is not null)
                    {
                        Harness.Interaction.InteractionEventTypes.Register();
                        var id = Guid.NewGuid().ToString("D");
                        exec.Session.Append(new Harness.Interaction.ApprovalAskedEvent
                        {
                            Id = id,
                            ToolName = exec.Name,
                            CallId = exec.CallId.Value,
                            Reason = merged.Reason,
                        });
                        exec.Session.Append(new Harness.Interaction.ApprovalDecidedEvent
                        {
                            Id = id,
                            Outcome = Harness.Interaction.ApprovalOutcome.Rejected,
                        });
                    }
                    return new DenyDecision($"the user rejected tool \"{exec.Name}\"");
                }
                return await next();
            })));

        // PostToolUse → PostToolDecision. Matcher subject is the tool name.
        _disposers.Add(_ctx.On("tools/post-execute",
            new Func<ToolRunContext, ToolExecutionResult, Func<Task<PostToolDecision>>, Task<PostToolDecision>>(async (exec, result, next) =>
            {
                var merged = await RunPointAsync("PostToolUse", exec.Name, BuildPostToolPayload(exec, result), exec.Session,
                    exec.Session is null ? null : LastTurn(exec.Session), exec.CancellationToken);
                var context = ContextFrom(merged);
                if (merged.Decision == "deny")
                {
                    // The recorded corpus appends the durable tool/result BEFORE the injected
                    // context splice, so the delivery is deferred to the session event.
                    QueueNextStep(exec.Session, exec.CallId, context);
                    return new BlockDecision(new ContentBlock[] { new TextBlock(merged.Reason ?? "blocked by PostToolUse hook") });
                }
                // Our hooks did not block; the post-tool context joins the next step after the
                // durable result (the port's tool decisions carry no additional-context slots).
                QueueNextStep(exec.Session, exec.CallId, context);
                return await next();
            })));

        // Deliver post-tool contexts once the durable tool/result event commits, so the recorded
        // event order (result, then the next-step context splice) reproduces exactly.
        _disposers.Add(_ctx.On("session/event", new Action<Harness.Session.Session, SessionEvent>((session, evt) =>
        {
            if (evt is not ToolResultEvent toolResult) return;
            var callId = (toolResult.Message.Source as ToolSource)?.CallId;
            if (callId is null) return;
            if (!_pendingContext.Remove((session.Id.Value, callId.Value), out var context)) return;
            _loop?.GetLoop(session.Id)?.Inject(context);
        })));

        // A blocking Stop hook steers at the stopping boundary, which makes the machine observe
        // pending input and run another step (the TS's stop-loop-guard TODO applies).
        _disposers.Add(_ctx.On("agent/turn-stopping", new Action<TurnStoppingProposal>(proposal =>
        {
            var merged = RunPointAsync("Stop", "", BuildStopPayload(proposal.Agent), proposal.Agent.Session, proposal.Turn, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (merged.Decision == "deny")
            {
                var text = merged.Reason ?? "continue: blocked by Stop hook";
                _loop?.GetLoop(proposal.Agent.Session.Id)?.Steer(PluginMessage(text));
            }
        })));
    }

    /// <summary>Run every command hook configured for <paramref name="point"/> whose matcher selects
    /// <paramref name="matchQuery"/>, with the per-event <paramref name="payload"/> on stdin, and
    /// fold the results. Writes a <c>hook/invoked</c>/<c>hook/result</c> pair per hook when
    /// <paramref name="turn"/> names an open turn. Detached lifecycle points omit the pair.</summary>
    private async Task<MergedHookOutcome> RunPointAsync(string point, string matchQuery, object payload,
        Harness.Session.Session? session, long? turn, CancellationToken signal)
    {
        if (!_groups.TryGetValue(point, out var groups)) return NeutralOutcome();
        var outputs = new List<HookOutput>();
        // CLAUDE_PROJECT_DIR: an explicit config value wins; the session-header cwd the TS falls
        // back to does not exist in the port's header vocabulary (documented reduction).
        var hookEnv = _config.ProjectDir is null
            ? null
            : (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["CLAUDE_PROJECT_DIR"] = _config.ProjectDir };
        foreach (var group in groups)
        {
            if (!HookMatcher.Matches(group.Matcher, matchQuery, MatcherMode.ClaudeCode)) continue;
            foreach (var hook in group.Hooks)
            {
                var handlerId = NextHandlerId(point);
                if (session is not null && turn is not null)
                {
                    HookLog.AppendInvoked(session, turn.Value, point, HookDialect.ClaudeCode, handlerId, group.Matcher);
                }
                var result = HookRunner.RunHook(_shell, hook, new HookRunner.RunHookOptions(
                    payload, hookEnv, Cwd: null, signal, TrailingNewline: true, _config.DefaultTimeoutMs, point));
                if (result.Output.UpdatedInput is { Count: > 0 })
                {
                    _ctx.Logger.Warn($"hooks-claude-code: {point} hook requested updatedInput, which is not yet honored (ignored)");
                }
                if (result.Output.SystemMessage is not null)
                {
                    _ctx.Logger.Warn($"hooks-claude-code: {point} hook emitted a systemMessage, which is not yet surfaced (ignored)");
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

    private string NextHandlerId(string point) => $"claude-code:{point}:{++_handlerCounter}";

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

    private void QueueNextStep(Harness.Session.Session? session, ToolCallId callId, UserMessage? context)
    {
        if (context is null || session is null) return;
        _pendingContext[(session.Id.Value, callId.Value)] = context;
    }

    private void InjectNextStep(Harness.Session.Session? session, UserMessage? context)
    {
        if (context is null || session is null) return;
        _loop?.GetLoop(session.Id)?.Inject(context);
    }

    private static long LastTurn(Harness.Session.Session session)
        => session.Events.OfType<TurnStartEvent>().Select(evt => evt.Turn).DefaultIfEmpty(0).Last();

    private Dictionary<string, object?> Base(Harness.Agent.Agent? agent, string eventName)
    {
        var session = agent?.Session;
        return new Dictionary<string, object?>
        {
            ["session_id"] = session?.Id.Value ?? "",
            ["transcript_path"] = session is null ? "" : (_persistence?.LogPath(session.Id) ?? ""),
            // The port's session header carries no workspace cwd (documented reduction).
            ["cwd"] = Environment.CurrentDirectory,
            ["hook_event_name"] = eventName,
        };
    }

    private Dictionary<string, object?> BuildSessionStartPayload(SessionStartPayload payload)
        => new(Base(payload.Agent, "SessionStart")) { ["source"] = payload.Source };

    private Dictionary<string, object?> BuildPromptPayload(PreStepProposal proposal)
        => new(Base(proposal.Agent, "UserPromptSubmit"))
        {
            ["prompt"] = BlocksToText(proposal.Messages.SelectMany(message => message.Content)),
        };

    private Dictionary<string, object?> BuildPreToolPayload(ToolRunContext exec)
        => new(Base(exec.Session is null ? null : AgentOf(exec.Session), "PreToolUse"))
        {
            ["tool_name"] = exec.Name,
            ["tool_input"] = exec.Arguments.Clone(),
            ["tool_use_id"] = exec.CallId.Value,
        };

    private Dictionary<string, object?> BuildPostToolPayload(ToolRunContext exec, ToolExecutionResult result)
        => new(Base(exec.Session is null ? null : AgentOf(exec.Session), "PostToolUse"))
        {
            ["tool_name"] = exec.Name,
            ["tool_input"] = exec.Arguments.Clone(),
            ["tool_use_id"] = exec.CallId.Value,
            ["tool_response"] = BlocksToText(result.Content),
        };

    private Dictionary<string, object?> BuildStopPayload(Harness.Agent.Agent agent)
        => new(Base(agent, "Stop")) { ["stop_hook_active"] = false };

    private Harness.Agent.Agent? AgentOf(Harness.Session.Session session)
        => _ctx.Get<Harness.Agent.AgentRegistry>("agents")?.List().FirstOrDefault(agent => ReferenceEquals(agent.Session, session));

    private static string BlocksToText(IEnumerable<ContentBlock> content)
        => string.Concat(content.OfType<TextBlock>().Select(block => block.Text));
}
