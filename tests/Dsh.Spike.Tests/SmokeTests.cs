using System.Text;
using System.Text.RegularExpressions;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Spike;
using Dsh.Todo;
using Dsh.Tools;

namespace Dsh.Spike.Tests;

/// <summary>Phase 0 part-2 smoke tests: the 22-event turn, the waterfall probe, and the exact stdout.</summary>
public static class SmokeTests
{
    private static (Context Context, LlmRuntime Llm, ToolRuntime Tools, MockLlmProvider Mock, Dsh.Session.Session Session) Boot()
    {
        var context = new Context();
        var sessions = new SessionStore(context);
        var llm = new LlmRuntime(context);
        var tools = new ToolRuntime(context);
        var mock = new MockLlmProvider();
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, mock);
        var todoService = new TodoService(context, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(context, allowParallelInProgress: false));
        var session = sessions.Create();
        return (context, llm, tools, mock, session);
    }

    private static UserMessage User() => new()
    {
        Id = new MessageId("msg-user-1"),
        Content = new ContentBlock[] { new TextBlock("Record your plan for the .NET spike as todos.") },
        Source = new UserSource(),
    };

    public static async Task TurnAppendsTwentyTwoEvents_WithTheExpectedSequence()
    {
        var (context, llm, tools, mock, session) = Boot();
        try
        {
            await HeadlessTurnDriver.RunOneTurnAsync(session, llm, tools, User());

            Assert.Equal(22, session.Events.Count);
            Assert.Equal(2, mock.CallCount);
            Assert.Equal(new[]
            {
                "turn/start", "step/start", "user/message", "request/header", "request/context",
                "assistant/chunk", "assistant/chunk", "assistant/chunk", "assistant/chunk", "assistant/message",
                "tool/call", "todo/write", "tool/result", "step/end", "step/start",
                "assistant/chunk", "assistant/chunk", "assistant/chunk", "assistant/chunk", "assistant/message",
                "step/end", "turn/end",
            }, session.Events.Select(e => e.Type).ToArray());
            Assert.Equal(0L, session.Events[0].Seq);
            Assert.Equal(21L, session.Events[^1].Seq);
            Assert.Equal("evt-0", session.Events[0].Id);
            Assert.Equal("evt-21", session.Events[^1].Id);
            Assert.True(session.Events[^1] is TurnEndEvent { Reason: CompletedReason }, "the turn must end completed");
        }
        finally
        {
            context.Dispose();
        }
    }

    public static async Task WaterfallProbe_ShortCircuits_AndEffectUnwind_RestoresAdapter()
    {
        var (context, llm, tools, mock, session) = Boot();
        try
        {
            await HeadlessTurnDriver.RunOneTurnAsync(session, llm, tools, User());
            Assert.Equal(2, mock.CallCount);

            var probe = context.On("llm/stream", new Func<GenerateOptions, Func<IAsyncEnumerable<StreamChunk>>, IAsyncEnumerable<StreamChunk>>((_, _) => ProbeStream()));
            var probeRequest = new GenerateOptions(MockLlmProvider.Provider, MockLlmProvider.Model, Array.Empty<Message>());
            var probeChunks = 0;
            await foreach (var _ in llm.Stream(probeRequest, CancellationToken.None)) probeChunks++;
            Assert.Equal(2, probeChunks);
            Assert.Equal(2, mock.CallCount); // short-circuit: the adapter was never invoked

            probe.Dispose();
            var afterChunks = 0;
            await foreach (var _ in llm.Stream(probeRequest, CancellationToken.None)) afterChunks++;
            Assert.Equal(4, afterChunks);
            Assert.Equal(3, mock.CallCount); // effect unwind: the adapter path is restored
        }
        finally
        {
            context.Dispose();
        }
    }

    public static void Dispose_UnwindsEveryEffect()
    {
        var context = new Context();
        var sessions = new SessionStore(context);
        var llm = new LlmRuntime(context);
        var tools = new ToolRuntime(context);
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, new MockLlmProvider());
        var todoService = new TodoService(context, false);
        tools.Register(TodoTool.Definition(context, false));
        var session = sessions.Create();
        context.Dispose();

        Assert.True(context.IsDisposed);
        Assert.Null(sessions.Get(session.Id));
        Assert.Null(tools.Get("todo_write"));
        Assert.Empty(llm.ListProviders());
        Assert.Equal(0, session.Events.Count);
        Assert.Empty(context.Registry.ServiceKeys);
    }

    public static void Smoke_ProducesTheExactExpectedStdout()
    {
        var writer = new StringWriter { NewLine = "\n" };
        SmokeScenario.RunAsync(writer).GetAwaiter().GetResult();
        var expected = NormalizeLines(ExpectedStdout);
        var actual = NormalizeLines(writer.ToString());
        Assert.Equal(expected.Length, actual.Length, "expected " + expected.Length + " lines but got " + actual.Length);
        for (var i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail("line " + i + " differs:\n  expected: " + expected[i] + "\n  actual:   " + actual[i]);
            }
        }
    }

    private static async IAsyncEnumerable<StreamChunk> ProbeStream()
    {
        yield return new BlockStart(0, "text");
        yield return new Finish(new Stop());
    }

    /// <summary>Normalize CRLF and column-padding runs so the fixture pins content, not padding width.</summary>
    private static string[] NormalizeLines(string text)
        => text.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, " {2,}", " "))
            .ToArray();

    /// <summary>The pinned smoke stdout (spike-design.md section 6.2, probe counts corrected for the two-call turn).</summary>
    private const string ExpectedStdout =
"""
== Dsh.Spike headless smoke ==
context booted; services: sessions, llm, tools, todo
session created: session-1
[00] turn/start {"turn":1}
[01] step/start {"turn":1,"step":1}
[02] user/message {"message":{"content":[{"type":"text","text":"Record your plan for the .NET spike as todos."}],"source":{"kind":"user"},"role":"user","id":"msg-user-1"},"surfaceOp":"append"}
[03] request/header {"header":{"config":{"provider":"mock","model":"mock-todo"},"system":"You are the Dsh port spike assistant.","tools":[{"name":"todo_write","description":"Record and update a structured task list for the current work. Send the ENTIRE list every call — it REPLACES the previous list (there are no partial updates, no per-item edits). Use it to plan multi-step work and show progress: add one todo per concrete step before you start. Keep AT MOST ONE todo `in_progress` at a time; while work remains, exactly one active task should be `in_progress`. Mark a todo `completed` the moment it is done (do not batch completions), and allow no `in_progress` item only once all work is complete. Skip the list for trivial single-step tasks. Statuses: `pending` (not started), `in_progress` (being worked on now), `completed` (finished).","parameters":{"todos":{"type":"array","required":true,"description":"The COMPLETE task list, replacing any previous list.","items":{"type":"object","additionalProperties":false,"properties":{"content":{"type":"string","required":true,"description":"What the task is — a short imperative line."},"status":{"type":"string","required":true,"enum":["pending","in_progress","completed"],"description":"pending (not started) | in_progress (now) | completed (done)."}}}}}}]},"reason":"initial"}
[04] request/context {"provider":"mock","model":"mock-todo"}
[05] assistant/chunk {"turn":1,"step":1,"chunk":{"type":"block-start","index":0,"blockType":"tool-call"}}
[06] assistant/chunk {"turn":1,"step":1,"chunk":{"type":"tool-call-delta","index":0,"id":"call-1","name":"todo_write","argumentsDelta":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}}
[07] assistant/chunk {"turn":1,"step":1,"chunk":{"type":"block-end","index":0,"block":{"type":"tool-call","id":"call-1","name":"todo_write","arguments":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}}}
[08] assistant/chunk {"turn":1,"step":1,"chunk":{"type":"finish","reason":{"kind":"tool-calls"}}}
[09] assistant/message {"turn":1,"step":1,"message":{"content":[{"type":"tool-call","id":"call-1","name":"todo_write","arguments":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}],"source":{"kind":"model","provider":"mock","model":"mock-todo"},"role":"assistant","id":"msg-assistant-1"},"surfaceOp":"append","sourceEventSeqs":[5,6,7,8]}
[10] tool/call {"turn":1,"step":1,"callId":"call-1","name":"todo_write","arguments":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}
[11] todo/write {"todos":[{"content":"Port the session event log","status":"in_progress"},{"content":"Port the mock LLM adapter","status":"pending"},{"content":"Port the todo tool","status":"pending"}]}
[12] tool/result {"turn":1,"step":1,"message":{"content":[{"type":"tool-result","toolCallId":"call-1","content":[{"type":"text","text":"Updated todo list: 2 pending, 1 in progress, 0 completed."}],"isError":false}],"source":{"kind":"tool","callId":"call-1"},"role":"user","id":"msg-tool-1"},"surfaceOp":"append","sourceEventSeqs":[10]}
[13] step/end {"turn":1,"step":1}
[14] step/start {"turn":1,"step":2}
[15] assistant/chunk {"turn":1,"step":2,"chunk":{"type":"block-start","index":0,"blockType":"text"}}
[16] assistant/chunk {"turn":1,"step":2,"chunk":{"type":"text-delta","index":0,"text":"Todo list recorded."}}
[17] assistant/chunk {"turn":1,"step":2,"chunk":{"type":"block-end","index":0,"block":{"type":"text","text":"Todo list recorded."}}}
[18] assistant/chunk {"turn":1,"step":2,"chunk":{"type":"finish","reason":{"kind":"stop"}}}
[19] assistant/message {"turn":1,"step":2,"message":{"content":[{"type":"text","text":"Todo list recorded."}],"source":{"kind":"model","provider":"mock","model":"mock-todo"},"role":"assistant","id":"msg-assistant-2"},"surfaceOp":"append","sourceEventSeqs":[15,16,17,18]}
[20] step/end {"turn":1,"step":2}
[21] turn/end {"turn":1,"reason":{"kind":"completed"}}
-- waterfall short-circuit probe --
probe stream: 2 chunks from listener; mock adapter calls still 2 (short-circuit OK)
listener disposed; next stream served by mock adapter (calls 3) (effect unwind OK)
-- context dispose --
sessions: store empty (session-1 detached; session/disposed emitted)
tools: todo_write unregistered (tools/change emitted)
llm: 0 adapters (llm/adapters-updated emitted)
session log: 22 events intact after dispose
== PASS ==
""";
}








