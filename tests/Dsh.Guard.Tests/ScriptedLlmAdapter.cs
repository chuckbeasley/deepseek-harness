using System.Runtime.CompilerServices;

namespace Dsh.Guard.Tests;

/// <summary>One scripted tool call a mock response streams.</summary>
/// <param name="CallId">the tool-call id.</param>
/// <param name="Name">the tool name.</param>
/// <param name="ArgumentsJson">the raw arguments JSON, exactly as a model would produce it.</param>
public sealed record ScriptedToolCall(string CallId, string Name, string ArgumentsJson);

/// <summary>One scripted model response: tool calls, or a plain text reply.</summary>
public sealed record ScriptedResponse(IReadOnlyList<ScriptedToolCall>? ToolCalls = null, string? Text = null)
{
    /// <summary>A response streaming one tool call.</summary>
    public static ScriptedResponse Tool(string callId, string name, string argumentsJson)
        => new(new[] { new ScriptedToolCall(callId, name, argumentsJson) });

    /// <summary>A response streaming one text block.</summary>
    public static ScriptedResponse TextResponse(string text) => new(null, text);
}

/// <summary>
/// Canned adapter for guard coverage: each <see cref="StreamAsync"/> call pops the next scripted
/// response — a tool-call block per <see cref="ScriptedToolCall"/>, or a plain text block — then
/// finishes. Deterministic ids keep assertions reproducible; no network is touched.
/// </summary>
public sealed class ScriptedLlmAdapter : ILlmAdapter
{
    /// <summary>The default registered provider route.</summary>
    public const string Provider = "mock";

    /// <summary>The model id served by this adapter.</summary>
    public const string Model = "mock-guard";

    private readonly ScriptedResponse[] _responses;
    private int _index;

    /// <summary>Create the adapter over one scripted sequence.</summary>
    public ScriptedLlmAdapter(params ScriptedResponse[] responses)
    {
        _responses = responses;
    }

    /// <summary>How many streams have started (incremented on the first MoveNextAsync).</summary>
    public int CallCount => _index;

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = _responses[_index++];
        if (response.ToolCalls is { Count: > 0 } calls)
        {
            var blockIndex = 0;
            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                var callId = new ToolCallId(call.CallId);
                yield return new BlockStart(blockIndex, "tool-call");
                yield return new ToolCallDelta(blockIndex, callId, call.Name, call.ArgumentsJson);
                yield return new BlockEnd(blockIndex, new ToolCallBlock(callId, call.Name, call.ArgumentsJson));
                blockIndex++;
            }
            yield return new Finish(new ToolCalls());
        }
        else
        {
            var text = response.Text ?? "done";
            yield return new BlockStart(0, "text");
            yield return new TextDelta(0, text);
            yield return new BlockEnd(0, new TextBlock(text));
            yield return new Finish(new Stop());
        }
    }
}
