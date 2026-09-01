using System.Text.Json;

namespace Harness.Guard.Tests;

/// <summary>
/// One booted headless spine with both guards installed: context, sessions, llm (scripted mock
/// adapter), tools (probe/other echo tools), system prompt, persistence (temp JSONL root, sync
/// append), the agent registry, the agent loop, the repeat-tool-reminder guard, and the
/// timeout-policy guard.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public required Context Ctx { get; init; }

    public required SessionStore Sessions { get; init; }

    public required LlmRuntime Llm { get; init; }

    public required ToolRuntime Tools { get; init; }

    public required SystemPromptService SystemPrompt { get; init; }

    public required AgentRegistry Agents { get; init; }

    public required global::Harness.AgentLoop.AgentLoop Loop { get; init; }

    public required SessionPersistenceService Persistence { get; init; }

    public required RepeatToolReminderGuard Reminder { get; init; }

    public required ToolTimeoutPolicy TimeoutPolicy { get; init; }

    public required string TempRoot { get; init; }

    /// <summary>Boot the full spine with a fresh temp persistence root.</summary>
    public static Harness Create(RepeatToolReminderConfig? reminder = null, ToolTimeoutConfig? timeout = null, bool attachPersistence = true)
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        var tools = new ToolRuntime(ctx);
        var systemPrompt = new SystemPromptService(ctx);
        var agents = new AgentRegistry(ctx);
        var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-guard-tests-" + Guid.NewGuid().ToString("N"));
        var persistence = new SessionPersistenceService(ctx, new PersistenceConfig { Root = tempRoot });
        if (attachPersistence) persistence.Attach(sessions);
        tools.Register(EchoTool("probe"));
        tools.Register(EchoTool("other"));
        var reminderGuard = new RepeatToolReminderGuard(ctx, reminder);
        var timeoutPolicy = new ToolTimeoutPolicy(ctx, timeout);
        var loop = new global::Harness.AgentLoop.AgentLoop(ctx);
        return new Harness
        {
            Ctx = ctx,
            Sessions = sessions,
            Llm = llm,
            Tools = tools,
            SystemPrompt = systemPrompt,
            Agents = agents,
            Loop = loop,
            Persistence = persistence,
            Reminder = reminderGuard,
            TimeoutPolicy = timeoutPolicy,
            TempRoot = tempRoot,
        };
    }

    /// <summary>Register the scripted adapter under the default mock route.</summary>
    public void RegisterAdapter(params ScriptedResponse[] responses)
    {
        Llm.RegisterAdapter(new[] { ScriptedLlmAdapter.Provider }, new ScriptedLlmAdapter(responses));
    }

    /// <summary>One user prompt message.</summary>
    public static UserMessage Prompt(string text)
        => Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(text) });

    /// <summary>Create and publish an agent on the mock route; returns handle, agent, and its loop driver.</summary>
    public (AgentHandle Handle, global::Harness.Agent.Agent Agent, LoopAgent Loop) CreateAgent(string id, string provider = ScriptedLlmAdapter.Provider)
    {
        var handle = Loop.Create(new SessionId(id), new AgentOptions { Provider = provider, Model = ScriptedLlmAdapter.Model });
        var loop = Loop.GetLoop(new SessionId(id)) ?? throw new InvalidOperationException($"no loop published for \"{id}\"");
        return (handle, handle.Agent, loop);
    }

    /// <summary>Every reminder the guard injected into one agent's session log, in log order.</summary>
    public static IReadOnlyList<UserMessageEvent> Reminders(global::Harness.Agent.Agent agent)
        => agent.Session.Events.OfType<UserMessageEvent>()
            .Where(evt => evt.Message.Source is PluginSource { Plugin: RepeatToolReminderGuard.GuardName })
            .ToArray();

    /// <summary>The joined text of one reminder message.</summary>
    public static string TextOf(UserMessageEvent reminder)
        => string.Join("|", reminder.Message.Content.OfType<TextBlock>().Select(block => block.Text));

    /// <summary>Dispose the context (unwinding every effect) and remove the temp persistence root.</summary>
    public ValueTask DisposeAsync()
    {
        Ctx.Dispose();
        if (Directory.Exists(TempRoot))
        {
            Directory.Delete(TempRoot, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private static ToolDefinition EchoTool(string name) => new(
        name,
        $"echo probe {name}",
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["ok"] = true })));
}
