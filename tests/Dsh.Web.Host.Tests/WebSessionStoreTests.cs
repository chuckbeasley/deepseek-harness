using Cordis.Core;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Spike;
using Dsh.SystemPrompt;
using Dsh.Tools;
using Dsh.Web.App.Store;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The web session store: the live running/queued/error projection over real mock-LLM turns and
/// the inbox event stream.
/// </summary>
public static class WebSessionStoreTests
{
    public static async Task Store_ProjectsQueuedCountsOverInboxEvents()
    {
        var ctx = Boot(out var sessions, out var loop);
        try
        {
            var store = new WebSessionStore(ctx);
            var id = new SessionId($"session-{Guid.NewGuid():N}");
            _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
            var driver = loop.GetLoop(id)!;
            var entry = store.Get(id);
            Assert.NotNull(entry, "the created session appears in the store");

            driver.Inject(NewMessage("first"));
            Assert.Equal(1, entry.Queued, "an injected message sits in the queue");
            driver.Inject(NewMessage("second"));
            Assert.Equal(2, entry.Queued, "each injection bumps the queued count");

            driver.Send(NewMessage("go"), InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync();
            Assert.Equal(0, entry.Queued, "a completed turn claims the queue");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Store_ProjectsRunningAndSummaryAfterTurn()
    {
        var ctx = Boot(out var sessions, out var loop);
        try
        {
            var store = new WebSessionStore(ctx);
            var id = new SessionId($"session-{Guid.NewGuid():N}");
            _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
            var driver = loop.GetLoop(id)!;
            var entry = store.Get(id);
            Assert.NotNull(entry, "the created session appears in the store");

            driver.Send(NewMessage("hello store"), InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync();
            Assert.False(entry.Running, "the driver is idle after the turn");
            Assert.Equal("Todo list recorded.", entry.Summary, "the mock reply projects as the summary");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Store_ProjectsTheLastAgentError_AndClearsOnActivity()
    {
        var ctx = Boot(out var sessions, out var loop);
        try
        {
            var store = new WebSessionStore(ctx);
            var id = new SessionId($"session-{Guid.NewGuid():N}");
            _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
            var driver = loop.GetLoop(id)!;
            var entry = store.Get(id);
            Assert.NotNull(entry, "the created session appears in the store");

            ctx.Emit("agent/error", new AgentErrorPayload(driver.Agent, 0, 0, new InvalidOperationException("boom")));
            Assert.Equal("boom", entry.Error, "the last agent failure message projects");

            driver.Send(NewMessage("recover"), InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync();
            Assert.Null(entry.Error, "a new activity clears the stale failure");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    /// <summary>Boot the loop spine with the mock adapter mounted (the session-store prerequisites).</summary>
    private static Context Boot(out SessionStore sessions, out Dsh.AgentLoop.AgentLoop loop)
    {
        var ctx = new Context();
        sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        var tools = new ToolRuntime(ctx);
        _ = new Dsh.Todo.TodoService(ctx, allowParallelInProgress: false);
        _ = tools.Register(Dsh.Todo.TodoTool.Definition(ctx, allowParallelInProgress: false));
        _ = new SystemPromptService(ctx);
        _ = new AgentRegistry(ctx);
        loop = new Dsh.AgentLoop.AgentLoop(ctx);
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, new MockLlmProvider());
        return ctx;
    }

    private static UserMessage NewMessage(string text)
        => new()
        {
            Id = new MessageId($"m-{Guid.NewGuid():N}"),
            Content = new ContentBlock[] { new TextBlock(text) },
            Source = new UserSource(),
        };
}
