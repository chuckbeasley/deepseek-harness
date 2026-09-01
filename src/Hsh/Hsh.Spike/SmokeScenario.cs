using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Session;
using Harness.Todo;
using Harness.Tools;

namespace Harness.Spike;

/// <summary>
/// The headless smoke (spike-design.md section 6): boot a Cordis context, register the
/// sessions/llm/tools services plus the mock adapter and the todo tool, run ONE turn, print the
/// session log in order, probe the llm/stream waterfall short-circuit, dispose the context, and
/// assert every effect unwound.
/// </summary>
public static class SmokeScenario
{
    private static readonly JsonSerializerOptions PrinterOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] EnvelopeProperties = { "Id", "Seq", "TimeMs", "Type" };

    public static async Task RunAsync(TextWriter output)
    {
        var context = new Context();
        int created = 0, disposed = 0, events = 0, toolsChange = 0, adaptersUpdated = 0;

        context.On("session/created", (Harness.Session.Session _) => created++);
        context.On("session/disposed", (Harness.Session.Session _) => disposed++);
        context.On("session/event", (Harness.Session.Session _, SessionEvent _) => events++);
        context.On("tools/change", () => toolsChange++);
        context.On("llm/adapters-updated", () => adaptersUpdated++);

        var sessions = new SessionStore(context);
        var llm = new LlmRuntime(context);
        var tools = new ToolRuntime(context);

        var mock = new MockLlmProvider();
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, mock);
        var todoService = new TodoService(context, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(context, allowParallelInProgress: false));

        output.WriteLine("== Harness.Spike headless smoke ==");
        output.WriteLine($"context booted; services: {string.Join(", ", context.Registry.ServiceKeys)}");

        var session = sessions.Create();
        output.WriteLine($"session created: {session.Id}");

        var userMessage = new UserMessage
        {
            Id = new MessageId("msg-user-1"),
            Content = new ContentBlock[] { new TextBlock("Record your plan for the .NET spike as todos.") },
            Source = new UserSource(),
        };
        await HeadlessTurnDriver.RunOneTurnAsync(session, llm, tools, userMessage);

        foreach (var evt in session.Events)
        {
            output.WriteLine($"[{evt.Seq:00}] {evt.Type,-20} {PayloadJson(evt)}");
        }

        // Waterfall short-circuit probe: a listener that never calls next() bypasses the adapter.
        output.WriteLine("-- waterfall short-circuit probe --");
        var probe = context.On("llm/stream", new Func<GenerateOptions, Func<IAsyncEnumerable<StreamChunk>>, IAsyncEnumerable<StreamChunk>>((_, _) => ProbeStream()));
        var probeRequest = new GenerateOptions(MockLlmProvider.Provider, MockLlmProvider.Model, Array.Empty<Message>());
        var probeChunkCount = 0;
        await foreach (var _ in llm.Stream(probeRequest, CancellationToken.None)) probeChunkCount++;
        Check(mock.CallCount == 2, "waterfall probe must not reach the adapter (short-circuit)");
        Check(probeChunkCount == 2, "probe stream must yield exactly 2 chunks");
        output.WriteLine($"probe stream: {probeChunkCount} chunks from listener; mock adapter calls still {mock.CallCount}   (short-circuit OK)");

        probe.Dispose();
        var afterChunkCount = 0;
        await foreach (var _ in llm.Stream(probeRequest, CancellationToken.None)) afterChunkCount++;
        Check(mock.CallCount == 3, "disposing the probe listener must restore the adapter path");
        Check(afterChunkCount == 4, "adapter stream must yield 4 chunks (call 3)");
        output.WriteLine($"listener disposed; next stream served by mock adapter (calls {mock.CallCount})     (effect unwind OK)");

        // Dispose and assert every effect unwound.
        output.WriteLine("-- context dispose --");
        context.Dispose();

        Check(context.IsDisposed, "context must be disposed");
        Check(sessions.Get(session.Id) is null, "session must be detached from the store");
        Check(disposed == 1, "session/disposed must have been emitted exactly once");
        Check(created == 1, "session/created must have been emitted exactly once");
        Check(events == 22, $"session/event must have been emitted for all 22 events (got {events})");
        Check(tools.Get("todo_write") is null, "todo_write must be unregistered");
        Check(toolsChange == 2, "tools/change must have been emitted on register and unregister");
        Check(llm.ListProviders().Count == 0, "llm must have zero adapters");
        Check(adaptersUpdated == 2, "llm/adapters-updated must have been emitted on register and unregister");
        Check(session.Events.Count == 22, "the session log must stay intact after dispose");

        output.WriteLine("sessions: store empty (session-1 detached; session/disposed emitted)");
        output.WriteLine("tools: todo_write unregistered (tools/change emitted)");
        output.WriteLine("llm: 0 adapters (llm/adapters-updated emitted)");
        output.WriteLine($"session log: {session.Events.Count} events intact after dispose");
        output.WriteLine("== PASS ==");
    }

    private static async IAsyncEnumerable<StreamChunk> ProbeStream()
    {
        yield return new BlockStart(0, "text");
        yield return new Finish(new Stop());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"smoke assertion failed: {message}");
    }

    /// <summary>
    /// Compact payload JSON for one event: the payload properties (envelope excluded) with
    /// camelCase keys, skipping nulls and false flags — the pinned fixture shape of section 6.2.
    /// </summary>
    private static string PayloadJson(SessionEvent evt)
    {
        var obj = new JsonObject();
        foreach (var property in evt.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            if (EnvelopeProperties.Contains(property.Name)) continue;
            var value = property.GetValue(evt);
            if (value is null) continue;
            if (value is bool flag && !flag) continue;
            obj[ToCamel(property.Name)] = JsonSerializer.SerializeToNode(value, property.PropertyType, PrinterOptions);
        }
        return obj.ToJsonString(PrinterOptions);
    }

    private static string ToCamel(string name)
        => char.ToLowerInvariant(name[0]) + name[1..];
}







