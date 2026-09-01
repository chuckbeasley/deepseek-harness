namespace Harness.Llm;

/// <summary>
/// Provider-wire adapter for the harness message and stream vocabulary. Implementations must
/// honor the supplied cancellation token and settle promptly after it aborts.
/// </summary>
public interface ILlmAdapter
{
    /// <summary>Stream one model call as raw chunks.</summary>
    IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, CancellationToken ct);
}
