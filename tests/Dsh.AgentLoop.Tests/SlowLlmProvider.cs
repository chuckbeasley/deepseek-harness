using System.Runtime.CompilerServices;

namespace Harness.AgentLoop.Tests;

/// <summary>
/// Canned adapter for cancellation coverage: yields one text block's start plus a partial delta,
/// then blocks forever until the call is cancelled.
/// </summary>
public sealed class SlowLlmProvider : ILlmAdapter
{
    /// <summary>How many streams have started.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, [EnumeratorCancellation] CancellationToken ct)
    {
        CallCount++;
        yield return new BlockStart(0, "text");
        yield return new TextDelta(0, "partial thought");
        await Task.Delay(Timeout.Infinite, ct);
    }
}
