using System.Text.Json;
using Dsh.Rpc.Generator;

namespace Dsh.Web.Host;

/// <summary>One RPC invocation: the canonical <c>&lt;namespace&gt;/&lt;method&gt;</c> endpoint and the wire args object.</summary>
public sealed record RpcRequest(
    /// <summary>Canonical endpoint (<c>session/list</c>, <c>session/prompt</c>, ...).</summary>
    string Endpoint,
    /// <summary>Wire args object; absent for parameterless calls.</summary>
    JsonElement? Args);

/// <summary>The exact answer to one invocation: a result or a coded failure, never both.</summary>
public sealed record RpcResponse(
    /// <summary>The business result JSON, or <c>null</c> when the call failed.</summary>
    JsonElement? Result,
    /// <summary>The coded failure, or <c>null</c> on success.</summary>
    RpcError? Error)
{
    /// <summary>Whether the call succeeded.</summary>
    public bool Ok => Error is null;
}

/// <summary>One Remote failure: the stable carrier code and a human message; details carry codec output.</summary>
[RpcCodec]
public sealed record RpcError(
    /// <summary>Stable machine code (the TS <c>RemoteErrorCode</c> union).</summary>
    string Code,
    /// <summary>Human-readable failure text.</summary>
    string Message,
    /// <summary>Optional structured details (validation issues, ...).</summary>
    JsonElement? Details = null);

/// <summary>
/// Universal Remote failure codes (port of the TS <c>RemoteErrorDetailsMap</c> universal carriers):
/// owner-side validation refused the request, the call was cancelled, or an unclassified Host
/// failure. The gateway merges its infrastructure codes next to these.
/// </summary>
public static class RpcErrorCodes
{
    /// <summary>Owner-side business validation refused the request.</summary>
    public const string BadRequest = "gateway/bad-request";

    /// <summary>The call was cancelled by the carrier signal or the backend.</summary>
    public const string Cancelled = "gateway/cancelled";

    /// <summary>Carrier, dispatch, or unclassified Host failure.</summary>
    public const string Internal = "gateway/internal";

    /// <summary>No active method exports the requested endpoint.</summary>
    public const string InvocationUnavailable = "gateway/invocation-unavailable";

    /// <summary>The wire args do not match the method's declared parameters.</summary>
    public const string ArgumentsInvalid = "gateway/arguments-invalid";

    /// <summary>The method's signature does not accept this call shape (unary vs stream).</summary>
    public const string SignatureInvalid = "gateway/signature-invalid";
}

/// <summary>
/// Owner-side validation refusal: thrown by a handler when the wire args are malformed or the
/// request is semantically invalid; the registry maps it to <c>gateway/bad-request</c>.
/// </summary>
public sealed class RpcBadRequestException : Exception
{
    /// <summary>Create the refusal with a safe message (never echoes secret or raw wire data).</summary>
    public RpcBadRequestException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A domain-coded failure thrown by a handler. The gateway transports <see cref="Code"/> and
/// <see cref="Details"/> verbatim: the TS <c>RemoteErrorCode</c> union is open, so the C# gateway
/// never validates the code.
/// </summary>
public sealed class RpcDomainError : Exception
{
    /// <summary>Create the coded failure with optional structured details.</summary>
    public RpcDomainError(string code, string message, JsonElement? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    /// <summary>Stable machine code, transported verbatim to the caller.</summary>
    public string Code { get; }

    /// <summary>Optional structured details (namespace ids, expected/actual revisions, ...).</summary>
    public JsonElement? Details { get; }
}
