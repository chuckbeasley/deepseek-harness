using System.Text.Json;

namespace Harness.Web.Host;

/// <summary>
/// One unary client envelope (port of the TS <c>client-request</c>): the client-minted rpc id,
/// the endpoint, and the payload. The payload for Remote calls is exactly <c>{ args: {...} }</c>.
/// </summary>
public sealed record ClientRequest(
    /// <summary>Client-minted UUID string, echoed verbatim.</summary>
    string RpcId,
    /// <summary>The endpoint (<c>namespace/method</c>), matching the URL.</summary>
    string Method,
    /// <summary>The payload object (<c>{ args: {...} }</c> for Remote calls).</summary>
    JsonElement Payload);

/// <summary>
/// One unary server envelope (port of the TS <c>server-response</c>): the echoed rpc id plus the
/// business result or the coded failure. The value slot is omitted when the business result is
/// absent (JSON has no <c>undefined</c>).
/// </summary>
public sealed record ServerResponse(
    /// <summary>The echoed rpc id (or <c>invalid-request</c> when the envelope carried none).</summary>
    string RpcId,
    /// <summary>The business result value, or <c>null</c> when absent or failed.</summary>
    JsonElement? Value,
    /// <summary>The coded failure, or <c>null</c> on success.</summary>
    RpcError? Error)
{
    /// <summary>Whether the call succeeded.</summary>
    public bool Ok => Error is null;
}

/// <summary>
/// Parse and validate one client envelope (port of the TS <c>clientRequestSchema</c> boundary
/// check). Failures carry the stable bad-request code with issue descriptions.
/// </summary>
public static class ClientRequestEnvelope
{
    /// <summary>Parse the request body into a client envelope.</summary>
    /// <param name="body">the parsed request body JSON.</param>
    /// <returns>the validated envelope, or the refusal describing what failed.</returns>
    public static (ClientRequest? Request, RpcError? Error) Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return (null, new RpcError(RpcErrorCodes.BadRequest, "invalid client-request message",
                JsonSerializer.SerializeToElement(new { issues = new object[] { new { message = "expected a JSON object" } } })));
        }
        if (!body.TryGetProperty("type", out var type) || type.GetString() != "client-request")
        {
            return (null, new RpcError(RpcErrorCodes.BadRequest, "invalid client-request message",
                JsonSerializer.SerializeToElement(new { issues = new object[] { new { message = "type must be \"client-request\"" } } })));
        }
        if (!body.TryGetProperty("rpcId", out var rpcId) || rpcId.ValueKind != JsonValueKind.String || rpcId.GetString()!.Length == 0)
        {
            return (null, new RpcError(RpcErrorCodes.BadRequest, "invalid client-request message",
                JsonSerializer.SerializeToElement(new { issues = new object[] { new { message = "rpcId must be a non-empty string" } } })));
        }
        if (!body.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || method.GetString()!.Length == 0)
        {
            return (null, new RpcError(RpcErrorCodes.BadRequest, "invalid client-request message",
                JsonSerializer.SerializeToElement(new { issues = new object[] { new { message = "method must be a non-empty string" } } })));
        }
        if (!body.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return (null, new RpcError(RpcErrorCodes.BadRequest, "invalid client-request message",
                JsonSerializer.SerializeToElement(new { issues = new object[] { new { message = "payload must be an object" } } })));
        }
        return (new ClientRequest(rpcId.GetString()!, method.GetString()!, payload.Clone()), null);
    }

    /// <summary>
    /// Validate the Remote payload shape: exactly one own key <c>args</c> whose value is a plain
    /// object.
    /// </summary>
    public static bool IsRemotePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        var argsFound = false;
        foreach (var property in payload.EnumerateObject())
        {
            count++;
            if (property.Name == "args" && property.Value.ValueKind == JsonValueKind.Object) argsFound = true;
        }
        return count == 1 && argsFound;
    }
}
