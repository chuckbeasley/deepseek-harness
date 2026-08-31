using Dsh.Llm;

namespace Dsh.Llm.Replay;

/// <summary>Replay adapter that serves one recorded stream per model call (port of the TS replay adapter).</summary>
internal sealed class ReplayAdapter : ILlmAdapter
{
    private readonly Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> _replay;

    public ReplayAdapter(Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> replay)
    {
        _replay = replay;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, CancellationToken ct)
        => _replay(request, ct);
}