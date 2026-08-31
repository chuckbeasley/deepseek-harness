using Cordis.Core;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Session.Projection;
using Dsh.Spike;
using Dsh.SystemPrompt;
using Dsh.Tools;
using Dsh.Web.Host;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The session control stream: the baseline cut (queues, jobs, projections) and the live queue
/// and jobs deltas, over real mock-LLM turns and a real jobs provider.
/// </summary>
public static class SessionControlTests
{
    public static async Task Control_BaselineShowsQueuedMessages_ThenQueueDelta()
    {
        var ctx = Boot(out var sessions, out var loop, out var agents);
        try
        {
            var id = new SessionId($"session-{Guid.NewGuid():N}");
            _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
            var driver = loop.GetLoop(id)!;
            var first = new UserMessage
            {
                Id = new MessageId($"m-{Guid.NewGuid():N}"),
                Content = new ContentBlock[] { new TextBlock("first queued") },
                Source = new UserSource(),
            };
            driver.Inject(first);

            var control = SessionControlRemotes.Control(ctx, sessions, agents, null, null);
            using var cts = new CancellationTokenSource();
            await using var enumerator = control.Invoke(null, cts.Token).GetAsyncEnumerator();

            Assert.True(await enumerator.MoveNextAsync(), "the baseline frame arrives first");
            var baseline = enumerator.Current;
            Assert.Equal("baseline", baseline.GetProperty("type").GetString());
            var queues = baseline.GetProperty("value").GetProperty("queues");
            var items = queues.GetProperty(id.Value);
            Assert.True(items.GetArrayLength() == 1, "the injected message sits in the baseline queue");
            Assert.Equal(first.Id.Value, items[0].GetProperty("id").GetString());
            Assert.Equal("queued", items[0].GetProperty("placement").GetString());
            Assert.True(items[0].GetProperty("message").GetProperty("content").GetArrayLength() == 1,
                "the queued item carries its content");

            var second = new UserMessage
            {
                Id = new MessageId($"m-{Guid.NewGuid():N}"),
                Content = new ContentBlock[] { new TextBlock("second queued") },
                Source = new UserSource(),
            };
            driver.Inject(second);
            Assert.True(await enumerator.MoveNextAsync(), "the queue delta arrives");
            var delta = enumerator.Current;
            Assert.Equal("queue", delta.GetProperty("type").GetString());
            Assert.Equal(id.Value, delta.GetProperty("sessionId").GetString());
            Assert.True(delta.GetProperty("items").GetArrayLength() == 2, "the delta carries the full current items");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Control_JobsBaselineAndDelta()
    {
        var ctx = Boot(out var sessions, out var loop, out var agents, out var jobs, out _);
        try
        {
            var id = new SessionId($"session-{Guid.NewGuid():N}");
            _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });

            var control = SessionControlRemotes.Control(ctx, sessions, agents, jobs, null);
            using var cts = new CancellationTokenSource();
            await using var enumerator = control.Invoke(null, cts.Token).GetAsyncEnumerator();

            Assert.True(await enumerator.MoveNextAsync(), "the baseline frame arrives first");
            Assert.Equal("baseline", enumerator.Current.GetProperty("type").GetString());
            Assert.True(enumerator.Current.GetProperty("value").GetProperty("jobs").GetProperty(id.Value).GetArrayLength() == 0,
                "no jobs yet at baseline");

            _ = jobs.Start(new JobStartRequest(
                Kind: "test",
                Label: "a control-stream job",
                Run: () => new JobHooks(
                    Cancel: _ => { },
                    Done: Task.FromResult(new JobOutcome(JobStatus.Completed, Detail: "done"))),
                OwnerSession: id.Value));

            var sawJob = false;
            var sawTerminal = false;
            var deadline = Environment.TickCount64 + 10000;
            while (Environment.TickCount64 < deadline && (!sawJob || !sawTerminal))
            {
                if (!await enumerator.MoveNextAsync()) break;
                var frame = enumerator.Current;
                if (frame.GetProperty("type").GetString() != "jobs") continue;
                if (frame.GetProperty("sessionId").GetString() != id.Value) continue;
                var jobList = frame.GetProperty("jobs");
                if (jobList.GetArrayLength() == 0) continue;
                sawJob = true;
                var job = jobList[0];
                Assert.Equal("test", job.GetProperty("kind").GetString());
                Assert.Equal("a control-stream job", job.GetProperty("label").GetString());
                Assert.True(job.TryGetProperty("startedAt", out _), "the wire carries the epoch-ms start");
                var status = job.GetProperty("status").GetString();
                if (status is "completed" or "killed" or "failed")
                {
                    Assert.Equal("done", job.GetProperty("detail").GetString());
                    Assert.True(job.TryGetProperty("finishedAt", out _), "the terminal job carries its finish");
                    sawTerminal = true;
                }
            }
            Assert.True(sawJob, "a jobs frame announces the registration");
            Assert.True(sawTerminal, "a jobs frame announces the terminal settlement");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Control_ProjectionsBaseline()
    {
        var ctx = Boot(out var sessions, out var loop, out var agents, out _, out var projections);
        try
        {
            var titleUnit = new ProjectionUnit<string?>
            {
                Init = () => null,
                Apply = (state, evt) => evt is UserMessageEvent user && state is null
                    ? user.Message.Content.OfType<TextBlock>().Select(block => block.Text).FirstOrDefault()
                    : state,
                View = state => state,
            };
            _ = projections.Register("title", titleUnit);

            var id = new SessionId($"session-{Guid.NewGuid():N}");
            _ = loop.Create(id, new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
            var driver = loop.GetLoop(id)!;
            var message = new UserMessage
            {
                Id = new MessageId($"m-{Guid.NewGuid():N}"),
                Content = new ContentBlock[] { new TextBlock("hello from the control stream test") },
                Source = new UserSource(),
            };
            driver.Send(message, InboxTarget.NextTurn, wakeup: true);
            await driver.WhenIdleAsync();

            var control = SessionControlRemotes.Control(ctx, sessions, agents, null, projections);
            using var cts = new CancellationTokenSource();
            await using var enumerator = control.Invoke(null, cts.Token).GetAsyncEnumerator();

            Assert.True(await enumerator.MoveNextAsync(), "the baseline frame arrives first");
            var baseline = enumerator.Current;
            Assert.Equal("baseline", baseline.GetProperty("type").GetString());
            var projection = baseline.GetProperty("value").GetProperty("projections").GetProperty(id.Value);
            Assert.True(projection.GetProperty("asOfSeq").GetInt64() >= 0, "the cut has a watermark");
            Assert.Equal("hello from the control stream test",
                projection.GetProperty("values").GetProperty("title").GetString());
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Control_EndsOnCancellation()
    {
        var ctx = Boot(out var sessions, out var loop, out var agents);
        try
        {
            var control = SessionControlRemotes.Control(ctx, sessions, agents, null, null);
            using var cts = new CancellationTokenSource();
            await using var enumerator = control.Invoke(null, cts.Token).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync(), "the baseline frame arrives first");
            cts.Cancel();
            Assert.False(await enumerator.MoveNextAsync(), "cancellation ends the stream");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    /// <summary>Boot the loop spine with the mock adapter, plus optional jobs and projection services.</summary>
    private static Context Boot(
        out SessionStore sessions,
        out Dsh.AgentLoop.AgentLoop loop,
        out AgentRegistry agents,
        out LocalJobsProvider? jobs,
        out SessionProjectionRegistry? projections)
    {
        var ctx = new Context();
        sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new ToolRuntime(ctx);
        _ = new SystemPromptService(ctx);
        agents = new AgentRegistry(ctx);
        loop = new Dsh.AgentLoop.AgentLoop(ctx);
        jobs = new LocalJobsProvider(ctx);
        projections = new SessionProjectionRegistry(ctx);
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, new MockLlmProvider());
        return ctx;
    }

    private static Context Boot(out SessionStore sessions, out Dsh.AgentLoop.AgentLoop loop, out AgentRegistry agents)
        => Boot(out sessions, out loop, out agents, out _, out _);
}
