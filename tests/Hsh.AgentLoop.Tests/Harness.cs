namespace Harness.AgentLoop.Tests;

/// <summary>
/// One booted headless spine: context, sessions, llm (mock adapter), tools (todo_write), system
/// prompt, persistence (temp JSONL root, sync append), the agent registry, and the agent loop.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public required Context Ctx { get; init; }

    public required SessionStore Sessions { get; init; }

    public required LlmRuntime Llm { get; init; }

    public required ToolRuntime Tools { get; init; }

    public required SystemPromptService SystemPrompt { get; init; }

    public required AgentRegistry Agents { get; init; }

    public required AgentLoop Loop { get; init; }

    public required MockLlmProvider Mock { get; init; }

    public required SessionPersistenceService Persistence { get; init; }

    public required string TempRoot { get; init; }

    /// <summary>Boot the full spine with a fresh temp persistence root.</summary>
    public static Harness Create(bool attachPersistence = true)
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        var tools = new ToolRuntime(ctx);
        var systemPrompt = new SystemPromptService(ctx);
        var agents = new AgentRegistry(ctx);
        var tempRoot = Path.Combine(Path.GetTempPath(), "hsh-agentloop-tests-" + Guid.NewGuid().ToString("N"));
        var persistence = new SessionPersistenceService(ctx, new PersistenceConfig { Root = tempRoot });
        var mock = new MockLlmProvider();
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, mock);
        var todoService = new TodoService(ctx, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(ctx, allowParallelInProgress: false));
        if (attachPersistence) persistence.Attach(sessions);
        var loop = new AgentLoop(ctx);
        return new Harness
        {
            Ctx = ctx,
            Sessions = sessions,
            Llm = llm,
            Tools = tools,
            SystemPrompt = systemPrompt,
            Agents = agents,
            Loop = loop,
            Mock = mock,
            Persistence = persistence,
            TempRoot = tempRoot,
        };
    }

    /// <summary>One user prompt message.</summary>
    public static UserMessage Prompt(string text)
        => Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(text) });

    /// <summary>Create and publish an agent on the mock route; returns handle, agent, and its loop driver.</summary>
    public (AgentHandle Handle, global::Harness.Agent.Agent Agent, LoopAgent Loop) CreateAgent(string id)
    {
        var handle = Loop.Create(new SessionId(id), new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
        var loop = Loop.GetLoop(new SessionId(id)) ?? throw new InvalidOperationException($"no loop published for \"{id}\"");
        return (handle, handle.Agent, loop);
    }

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
}
