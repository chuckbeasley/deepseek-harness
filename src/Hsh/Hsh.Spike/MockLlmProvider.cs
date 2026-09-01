using System.Runtime.CompilerServices;
using Harness.Llm;

namespace Harness.Spike;

/// <summary>
/// Canned two-phase adapter: call 1 streams exactly one todo_write tool call; later calls stream a
/// plain-text block and stop. Deterministic ids keep the smoke fixture reproducible.
/// </summary>
public sealed class MockLlmProvider : ILlmAdapter
{
    /// <summary>The registered provider route.</summary>
    public const string Provider = "mock";

    /// <summary>The model id served by this provider.</summary>
    public const string Model = "mock-todo";

    /// <summary>Fixture-fixed tool-call id.</summary>
    public const string ToolCallIdValue = "call-1";

    /// <summary>Fixture-fixed raw tool-call arguments (exactly one in_progress item).</summary>
    public const string ToolCallArguments =
        "{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}";

    /// <summary>How many streams have started (incremented on the first MoveNextAsync).</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        if (CallCount == 1)
        {
            var callId = new ToolCallId(ToolCallIdValue);
            yield return new BlockStart(0, "tool-call");
            ct.ThrowIfCancellationRequested();
            yield return new ToolCallDelta(0, callId, "todo_write", ToolCallArguments);
            yield return new BlockEnd(0, new ToolCallBlock(callId, "todo_write", ToolCallArguments));
            yield return new Finish(new ToolCalls());
        }
        else
        {
            yield return new BlockStart(0, "text");
            yield return new TextDelta(0, "Todo list recorded.");
            yield return new BlockEnd(0, new TextBlock("Todo list recorded."));
            yield return new Finish(new Stop());
        }
    }
}


