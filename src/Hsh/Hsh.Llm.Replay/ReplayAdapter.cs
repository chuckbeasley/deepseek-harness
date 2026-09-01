using Harness.Llm;

namespace Harness.Llm.Replay;

/// <summary>
/// Replay adapter that serves one recorded stream per model call (port of the TS replay adapter),
/// and exposes the recorded model capability metadata for request-config resolution.
/// </summary>
internal sealed class ReplayAdapter : ILlmAdapter, IAdapterModelMetadata
{
    private readonly Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> _replay;

    private readonly IReadOnlyDictionary<string, LlmModelMetadata>? _models;

    public ReplayAdapter(
        Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> replay,
        IReadOnlyDictionary<string, LlmModelMetadata>? models = null)
    {
        _replay = replay;
        _models = models;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, CancellationToken ct)
        => _replay(request, ct);

    /// <inheritdoc />
    public LlmModelMetadata? ResolveModel(string model)
        => _models is not null && _models.TryGetValue(model, out var metadata) ? metadata : null;
}