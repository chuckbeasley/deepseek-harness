using Harness.Cordis.Core;
using Harness.Agent;
using Harness.AgentLoop;
using Harness.Llm;
using Harness.Session;
using Harness.Session.Persistence;
using Harness.SystemPrompt;
using Harness.Todo;
using Harness.Tools;

namespace Harness.Spike;

/// <summary>
/// The Phase 2 gate smoke: one headless task end-to-end through the real <see cref="AgentLoop"/>
/// against the mock provider (tool call, then text), with the JSONL session log persisting during
/// the turn and replaying identically afterwards.
/// </summary>
public static class Phase2Scenario
{
    public static async Task RunAsync(TextWriter output)
    {
        var context = new Context();
        var sessions = new SessionStore(context);
        var llm = new LlmRuntime(context);
        var tools = new ToolRuntime(context);
        var systemPrompt = new SystemPromptService(context);
        var agents = new AgentRegistry(context);
        var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-phase2-smoke-" + Guid.NewGuid().ToString("N"));
        var persistence = new SessionPersistenceService(context, new PersistenceConfig { Root = tempRoot });
        var mock = new MockLlmProvider();
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, mock);
        var todoService = new TodoService(context, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(context, allowParallelInProgress: false));
        persistence.Attach(sessions);
        var loop = new Harness.AgentLoop.AgentLoop(context);

        try
        {
            output.WriteLine("== Harness.Phase2 agent-loop smoke ==");
            var handle = loop.Create(
                new SessionId("session-phase2"),
                new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
            var agent = handle.Agent;
            var driver = loop.GetLoop(new SessionId("session-phase2"))
                ?? throw new InvalidOperationException("phase2 smoke: no loop published");
            output.WriteLine($"agent published: {agent.Id} (provider {agent.Options.Provider}, model {agent.Options.Model})");

            var task = new UserMessage
            {
                Id = new MessageId("msg-phase2-1"),
                Content = new ContentBlock[] { new TextBlock("Record your plan for the .NET port as todos.") },
                Source = new UserSource(),
            };
            driver.Send(task, InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync();

            foreach (var evt in agent.Session.Events)
            {
                output.WriteLine($"[{evt.Seq:00}] {evt.Type}");
            }

            Check(agent.Session.Events.Count == 22, $"the loop turn must log 22 events (got {agent.Session.Events.Count})");
            Check(mock.CallCount == 2, $"the mock must serve two streams (got {mock.CallCount})");
            Check(agent.Session.Events[^1] is TurnEndEvent { Reason: CompletedReason }, "the turn must end completed");
            Check(agent.Status == AgentStatus.Idle, "the agent must return to idle");

            // Persistence round-trip: the JSONL log replays the identical event sequence.
            var stored = persistence.Load(agent.Session.Id);
            Check(stored is not null, "the persisted log must exist");
            Check(stored!.Events.Count == agent.Session.Events.Count, "the persisted log must hold every event");
            Check(stored.Events.Select(evt => evt.Type).SequenceEqual(agent.Session.Events.Select(evt => evt.Type)),
                "the persisted log must replay the identical event sequence");

            output.WriteLine($"persistence: {stored.Events.Count} events replayed from JSONL (round-trip OK)");
            output.WriteLine("== PHASE2 PASS ==");
        }
        finally
        {
            context.Dispose();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"phase2 smoke assertion failed: {message}");
    }
}
