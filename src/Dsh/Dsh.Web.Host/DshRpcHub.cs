using Microsoft.AspNetCore.SignalR;

namespace Harness.Web.Host;

/// <summary>
/// The SignalR RPC hub (the web carrier of the Typert gateway): one generic invoke channel over
/// the registered method registry. The endpoint and args travel as plain wire values; failures
/// settle as coded <see cref="RpcResponse"/> values, exactly like the JSON-RPC carrier.
/// </summary>
public sealed class DshRpcHub : Hub
{
    private readonly DshRpcRegistry _registry;

    /// <summary>Create the hub over the mounted registry.</summary>
    public DshRpcHub(DshRpcRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Invoke one registered method and return its exact answer.</summary>
    /// <param name="endpoint">canonical <c>namespace/method</c> endpoint.</param>
    /// <param name="args">wire args object, or <c>null</c>.</param>
    public Task<RpcResponse> Invoke(string endpoint, System.Text.Json.JsonElement? args)
        => _registry.InvokeAsync(new RpcRequest(endpoint, args), Context.ConnectionAborted);
}
