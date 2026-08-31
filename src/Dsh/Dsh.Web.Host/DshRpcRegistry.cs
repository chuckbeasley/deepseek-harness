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
/// The RPC method registry (ctx.rpc): one method per endpoint, registered as effects, with the
/// exact Typert failure vocabulary on dispatch. Registrations are effects: disposing the context
/// (or the returned disposer) withdraws the method.
/// </summary>
public sealed class DshRpcRegistry : Service
{
    private readonly Dictionary<string, RpcMethod> _methods = new(StringComparer.Ordinal);
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

    /// <summary>Every registered method, in registration order.</summary>
    public IReadOnlyList<RpcMethod> List()
    {
        lock (_gate) return _methods.Values.ToArray();
    }

    /// <summary>
    /// Dispatch one invocation. Failures settle as coded <see cref="RpcError"/> responses, never
    /// as carrier exceptions: an unknown endpoint is <c>gateway/method-not-found</c>, a handler
    /// rejection is <c>gateway/internal</c>, and cancellation is <c>gateway/cancelled</c>.
    /// </summary>
    /// <param name="request">the invocation.</param>
    /// <param name="cancellationToken">carrier cancellation.</param>
    /// <returns>the exact answer.</returns>
    public async Task<RpcResponse> InvokeAsync(RpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RpcMethod? method;
        lock (_gate) method = _methods.GetValueOrDefault(request.Endpoint);
        if (method is null)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.MethodNotFound, $"no rpc method is registered for \"{request.Endpoint}\""));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.Cancelled, "the rpc call was cancelled"));
        }
        catch (Exception error)
        {
            return new RpcResponse(null, new RpcError(RpcErrorCodes.Internal, error.Message));
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
