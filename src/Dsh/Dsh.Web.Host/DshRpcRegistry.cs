using System.Text.Json;
using Cordis.Core;

namespace Dsh.Web.Host;

/// <summary>
/// One registered RPC method (the runtime half of the Typert local registry port): the canonical
/// endpoint and the invoking delegate. The wire args are decoded by the registration's own
/// handler — the C# source generator arrives with a later wave, so the boundary validation lives
/// in the handlers until then (documented reduction).
/// </summary>
public sealed record RpcMethod(
    /// <summary>Canonical endpoint (<c>namespace/method</c>).</summary>
    string Endpoint,
    /// <summary>Invoke the method.</summary>
    /// <param name="args">the wire args object, or <c>null</c> for parameterless calls.</param>
    /// <param name="cancellationToken">carrier cancellation.</param>
    /// <returns>the business result JSON, or <c>null</c>.</returns>
    Func<JsonElement?, CancellationToken, Task<JsonElement?>> Invoke);

/// <summary>
/// One registered stream method (the Typert <c>mode: 'stream'</c> descriptor): the canonical
/// endpoint and a yielding invocation. Stream methods are opened only through the mux; a unary
/// dispatch on one settles <c>gateway/signature-invalid</c>.
/// </summary>
public sealed record RpcStreamMethod(
    /// <summary>Canonical endpoint (<c>namespace/method</c>).</summary>
    string Endpoint,
    /// <summary>Invoke the stream.</summary>
    /// <param name="args">the wire args object, or <c>null</c> for parameterless calls.</param>
    /// <param name="cancellationToken">carrier cancellation.</param>
    /// <returns>the yielded item sequence.</returns>
    Func<JsonElement?, CancellationToken, IAsyncEnumerable<JsonElement>> Invoke);

/// <summary>
/// The RPC method registry (ctx.rpc): one method per endpoint, registered as effects, with the
/// exact Typert failure vocabulary on dispatch. Registrations are effects: disposing the context
/// (or the returned disposer) withdraws the method.
/// </summary>
public sealed class DshRpcRegistry : Service
{
    private readonly Dictionary<string, RpcMethod> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RpcStreamMethod> _streams = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Create and register the registry under the <c>rpc</c> key.</summary>
    public DshRpcRegistry(Context ctx)
        : base(ctx, "rpc")
    {
    }

    /// <summary>
    /// Register one method. One handler per endpoint: two registrations of the same endpoint
    /// would make dispatch ambiguous.
    /// </summary>
    /// <returns>the disposer that withdraws the method.</returns>
    /// <exception cref="ArgumentException">when the endpoint is empty or already registered.</exception>
    public IDisposable Register(RpcMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Endpoint.Length == 0)
        {
            throw new ArgumentException("rpc: an endpoint must be non-empty", nameof(method));
        }
        if (!method.Endpoint.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"rpc: endpoint \"{method.Endpoint}\" must be namespace/method", nameof(method));
        }
        return Ctx.Effect(() =>
        {
            lock (_gate)
            {
                if (_methods.ContainsKey(method.Endpoint))
                {
                    throw new ArgumentException($"rpc: endpoint \"{method.Endpoint}\" is already registered", nameof(method));
                }
                _methods.Add(method.Endpoint, method);
            }
            return new ActionDisposer(() =>
            {
                lock (_gate) _methods.Remove(method.Endpoint);
            });
        }, $"rpc.register(\"{method.Endpoint}\")");
    }

    /// <summary>One registered method, or <c>null</c> when nothing claims the endpoint.</summary>
    public RpcMethod? Get(string endpoint)
    {
        lock (_gate) return _methods.GetValueOrDefault(endpoint);
    }

    /// <summary>
    /// Register one stream method. One handler per endpoint, and an endpoint cannot be both unary
    /// and stream.
    /// </summary>
    /// <returns>the disposer that withdraws the method.</returns>
    /// <exception cref="ArgumentException">when the endpoint is empty, malformed, or already claimed.</exception>
    public IDisposable RegisterStream(RpcStreamMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        ValidateEndpoint(method.Endpoint);
        return Ctx.Effect(() =>
        {
            lock (_gate)
            {
                if (_streams.ContainsKey(method.Endpoint))
                {
                    throw new ArgumentException($"rpc: endpoint \"{method.Endpoint}\" is already registered", nameof(method));
                }
                if (_methods.ContainsKey(method.Endpoint))
                {
                    throw new ArgumentException($"rpc: endpoint \"{method.Endpoint}\" is already registered as a unary method", nameof(method));
                }
                _streams.Add(method.Endpoint, method);
            }
            return new ActionDisposer(() =>
            {
                lock (_gate) _streams.Remove(method.Endpoint);
            });
        }, $"rpc.registerStream(\"{method.Endpoint}\")");
    }

    /// <summary>One registered stream method, or <c>null</c> when nothing claims the endpoint.</summary>
    public RpcStreamMethod? GetStream(string endpoint)
    {
        lock (_gate) return _streams.GetValueOrDefault(endpoint);
    }

    /// <summary>Whether the endpoint is registered as a stream method.</summary>
    public bool IsStream(string endpoint)
    {
        lock (_gate) return _streams.ContainsKey(endpoint);
    }

    /// <summary>Every registered method, in registration order.</summary>
    public IReadOnlyList<RpcMethod> List()
    {
        lock (_gate) return _methods.Values.ToArray();
    }

    /// <summary>
    /// Dispatch one invocation. Failures settle as coded <see cref="RpcError"/> responses, never
    /// as carrier exceptions: an unknown endpoint is <c>gateway/invocation-unavailable</c>, a
    /// handler rejection is <c>gateway/internal</c>, and cancellation is <c>gateway/cancelled</c>.
    /// </summary>
    /// <param name="request">the invocation.</param>
    /// <param name="cancellationToken">carrier cancellation.</param>
    /// <returns>the exact answer.</returns>
    public async Task<RpcResponse> InvokeAsync(RpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RpcMethod? method;
        lock (_gate)
        {
            method = _methods.GetValueOrDefault(request.Endpoint);
            if (method is null && _streams.ContainsKey(request.Endpoint))
            {
                return new RpcResponse(null, new RpcError(RpcErrorCodes.SignatureInvalid,
                    $"endpoint \"{request.Endpoint}\" is a stream method and cannot be invoked unary"));
            }
        }
        if (method is null)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.InvocationUnavailable, $"no rpc method is registered for \"{request.Endpoint}\""));
        }
        try
        {
            var result = await method.Invoke(request.Args, cancellationToken).ConfigureAwait(false);
            return new RpcResponse(result, null);
        }
        catch (RpcBadRequestException error)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.BadRequest, error.Message));
        }
        catch (RpcSessionNotFoundError error)
        {
            return new RpcResponse(null, new RpcError("session/not-found", error.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.Cancelled, "the rpc call was cancelled"));
        }
        catch (Exception error)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.Internal, error.Message));
        }
    }

    /// <summary>Validate the canonical endpoint contract; the stream variant shares it.</summary>
    private static void ValidateEndpoint(string endpoint)
    {
        if (endpoint.Length == 0)
        {
            throw new ArgumentException("rpc: an endpoint must be non-empty", nameof(endpoint));
        }
        if (!endpoint.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"rpc: endpoint \"{endpoint}\" must be namespace/method", nameof(endpoint));
        }
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync effect cleanups.</summary>
internal sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
