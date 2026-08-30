using Cordis.Core;

namespace Dsh.Llm;

/// <summary>
/// LLM service (ctx.llm): an adapter registry plus a streaming model-call API, interceptable via
/// the <c>llm/stream</c> waterfall. Adapter registrations are effects: the context unregisters
/// them on dispose.
/// </summary>
public sealed class LlmRuntime : Service
{
    private readonly Dictionary<string, ILlmAdapter> _adapters = new(StringComparer.Ordinal);

    public LlmRuntime(Context ctx)
        : base(ctx, "llm")
    {
    }

    /// <summary>
    /// Register an adapter for the given provider routes. All-or-nothing: an empty list, a
    /// duplicate route, or an invalid name throws and leaves the registry untouched.
    /// </summary>
    /// <returns>the disposer that unregisters every route and notifies topology observers.</returns>
    public IDisposable RegisterAdapter(string[] providers, ILlmAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(adapter);
        if (providers.Length == 0)
        {
            throw new LlmError("an adapter must register at least one provider", "INVALID_ADAPTER");
        }
        return Ctx.Effect(() =>
        {
            foreach (var provider in providers)
            {
                if (provider.Length == 0)
                {
                    throw new LlmError("adapter provider names must be non-empty", "INVALID_ADAPTER");
                }
                if (_adapters.ContainsKey(provider))
                {
                    throw new LlmError($"an adapter for provider \"{provider}\" is already registered", "DUPLICATE_ADAPTER");
                }
            }
            foreach (var provider in providers) _adapters[provider] = adapter;
            EmitAdaptersUpdated();
            return new ActionDisposer(() =>
            {
                foreach (var provider in providers) _adapters.Remove(provider);
                EmitAdaptersUpdated();
            });
        }, "llm.registerAdapter()");
    }

    /// <summary>Provider routes with a registered adapter, in registration order.</summary>
    public IReadOnlyList<string> ListProviders() => _adapters.Keys.ToArray();

    /// <summary>
    /// Stream one model call as raw chunks. The <c>llm/stream</c> waterfall wraps the resolved
    /// adapter; a listener that never calls <c>next()</c> short-circuits the chain.
    /// </summary>
    public IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Ctx.Waterfall<IAsyncEnumerable<StreamChunk>>(
            "llm/stream",
            new object?[] { options },
            () => AdapterStream(options, ct));
    }

    private IAsyncEnumerable<StreamChunk> AdapterStream(GenerateOptions options, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(options.Provider, out var adapter))
        {
            throw new LlmError($"no adapter registered for provider \"{options.Provider}\"", "NO_ADAPTER");
        }
        return adapter.StreamAsync(options, ct);
    }

    private void EmitAdaptersUpdated()
    {
        try
        {
            Ctx.Emit("llm/adapters-updated");
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"llm: llm/adapters-updated listener threw: {error.Message}");
        }
    }
}
