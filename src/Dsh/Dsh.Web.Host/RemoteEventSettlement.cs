using System.Text.Json;
using Cordis.Core;

namespace Dsh.Web.Host;

/// <summary>The kind of one settled remote waterfall outcome (the TS <c>RemoteEventResult</c> outcome union).</summary>
public enum RemoteEventOutcomeKind
{
    /// <summary>The client delegated: the waterfall continuation runs.</summary>
    Next,

    /// <summary>The client returned a value for the waterfall.</summary>
    Result,

    /// <summary>The client's listener rejected the proposal.</summary>
    Rejected,
}

/// <summary>One settled outcome for a pending remote waterfall proposal.</summary>
public sealed record RemoteEventOutcome(RemoteEventOutcomeKind Kind, JsonElement? Value = null, Exception? Error = null);

/// <summary>
/// The per-client registry of pending remote waterfall proposals (the C# half of the TS
/// <c>$events/result</c> settlement): a live <c>$events</c> stream registers one proposal per
/// eventId through <see cref="Begin"/>, the <c>$events/result</c> unary settles it through
/// <see cref="TrySettle"/> (the TS no-op for an unknown eventId on a known client), and a closing
/// stream cancels everything it owned. A registered resolver must never throw: it completes the
/// listener's own continuation.
/// </summary>
public sealed class RemoteEventSettlement
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Pending>> _byClient = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clients = new(StringComparer.Ordinal);

    private sealed class Pending
    {
        public required string EventId { get; init; }
        public required Func<RemoteEventOutcome, Task> Resolve { get; init; }
        public required Action Cancel { get; init; }
    }

    /// <summary>
    /// Register one active client generation (a live <c>$events</c> stream). A client with no
    /// pending proposals is still active: <c>$events/result</c> for an unknown eventId under it is
    /// the TS no-op ack, not an error.
    /// </summary>
    public void RegisterClient(string clientId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        lock (_gate) _clients.Add(clientId);
    }

    /// <summary>Drop one client generation and cancel everything it still owns.</summary>
    public void UnregisterClient(string clientId)
    {
        lock (_gate) _clients.Remove(clientId);
        CancelClient(clientId);
    }

    /// <summary>Whether any live client generation exists for one id.</summary>
    public bool HasClient(string clientId)
    {
        lock (_gate) return _clients.Contains(clientId);
    }

    /// <summary>
    /// Register one pending proposal. The resolver receives the settled outcome (next/result/
    /// rejected) and must complete the listener's continuation without throwing; the cancel action
    /// runs when the request or the stream dies first.
    /// </summary>
    /// <returns>the eventId the client correlates with.</returns>
    public string Begin(string clientId, Func<RemoteEventOutcome, Task> resolve, Action cancel)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(cancel);
        var eventId = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            if (!_byClient.TryGetValue(clientId, out var pending))
            {
                pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
                _byClient[clientId] = pending;
            }
            pending[eventId] = new Pending { EventId = eventId, Resolve = resolve, Cancel = cancel };
        }
        return eventId;
    }
    /// <summary>
    /// Settle one proposal. An unknown clientId reports an error (the TS
    /// <c>identifies no active event stream</c>); an unknown eventId on a known client is the TS
    /// no-op ack. The resolver runs detached: it completes the listener's continuation, never this
    /// call.
    /// </summary>
    public bool TrySettle(string clientId, string eventId, RemoteEventOutcome outcome, out string? error)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Pending? pending = null;
        lock (_gate)
        {
            if (!_byClient.TryGetValue(clientId, out var client))
            {
                // A registered client with no pending proposals is the TS no-op ack; an
                // unregistered client generation is an error.
                if (_clients.Contains(clientId))
                {
                    error = null;
                    return true;
                }
                error = $"clientId \"{clientId}\" identifies no active event stream";
                return false;
            }
            if (client.Remove(eventId, out pending))
            {
                if (client.Count == 0) _byClient.Remove(clientId);
            }
        }
        error = null;
        if (pending is not null)
        {
            _ = Task.Run(() => pending.Resolve(outcome));
        }
        return true;
    }

    /// <summary>Cancel one proposal (an aborted request or a closing stream); a no-op when it already settled.</summary>
    public void Cancel(string clientId, string eventId)
    {
        Pending? pending = null;
        lock (_gate)
        {
            if (_byClient.TryGetValue(clientId, out var client) && client.Remove(eventId, out pending)
                && client.Count == 0)
            {
                _byClient.Remove(clientId);
            }
        }
        pending?.Cancel();
    }

    /// <summary>Cancel every proposal one client generation owned (the stream closed).</summary>
    public void CancelClient(string clientId)
    {
        Pending[] pending;
        lock (_gate)
        {
            if (!_byClient.Remove(clientId, out var client)) return;
            pending = client.Values.ToArray();
        }
        foreach (var entry in pending) entry.Cancel();
    }

    /// <summary>
    /// The <c>$events/result</c> unary (port of the TS <c>REMOTE_EVENT_RESULT_ENDPOINT</c>): the
    /// client's answer to one delivered waterfall. The payload must be exactly
    /// <c>{clientId, eventId, outcome}</c>; an unknown clientId settles
    /// <c>gateway/internal</c>, an unknown eventId on a known client acks as a no-op (the TS), and
    /// a settled proposal answers <c>{settled: true}</c>.
    /// </summary>
    public static RpcMethod ResultMethod(RemoteEventSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        return new RpcMethod("$events/result", (args, ct) =>
        {
            if (!TryParseResult(args, out var clientId, out var eventId, out var outcome, out var parseError))
            {
                throw new RpcBadRequestException(parseError ?? "invalid payload for $events/result");
            }
            if (!settlement.HasClient(clientId))
            {
                throw new RpcDomainError(RpcErrorCodes.Internal,
                    $"clientId \"{clientId}\" identifies no active event stream",
                    JsonSerializer.SerializeToElement(new { }));
            }
            settlement.TrySettle(clientId, eventId, outcome, out _);
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { settled = true }));
        });
    }

    /// <summary>
    /// Parse and validate one result payload (port of the TS <c>parseRemoteEventResult</c>): exact
    /// keys <c>clientId</c>/<c>eventId</c>/<c>outcome</c>; outcome kinds <c>next</c>, <c>result</c>
    /// (optional JSON value), and <c>rejected</c> (with <c>{name, message, code?, details?}</c>).
    /// </summary>
    private static bool TryParseResult(JsonElement? args, out string clientId, out string eventId,
        out RemoteEventOutcome outcome, out string? error)
    {
        clientId = "";
        eventId = "";
        outcome = null!;
        error = "invalid Remote event result";
        if (args is not { } element || element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("clientId", out var clientValue) || clientValue.ValueKind != JsonValueKind.String
            || clientValue.GetString()!.Length == 0
            || !element.TryGetProperty("eventId", out var eventValue) || eventValue.ValueKind != JsonValueKind.String
            || eventValue.GetString()!.Length == 0
            || !element.TryGetProperty("outcome", out var outcomeValue) || outcomeValue.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var count = 0;
        foreach (var property in element.EnumerateObject()) count++;
        if (count != 3) return false;
        clientId = clientValue.GetString()!;
        eventId = eventValue.GetString()!;
        if (!outcomeValue.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        switch (kind.GetString())
        {
            case "next":
                if (HasOnlyKey(outcomeValue, "kind"))
                {
                    outcome = new RemoteEventOutcome(RemoteEventOutcomeKind.Next);
                    return true;
                }
                return false;
            case "result":
                if (HasOnlyKey(outcomeValue, "kind") || HasOnlyKeys(outcomeValue, "kind", "value"))
                {
                    outcome = new RemoteEventOutcome(RemoteEventOutcomeKind.Result,
                        outcomeValue.TryGetProperty("value", out var value) ? value.Clone() : null);
                    return true;
                }
                return false;
            case "rejected":
                if (outcomeValue.TryGetProperty("error", out var rejection) && rejection.ValueKind == JsonValueKind.Object
                    && HasOnlyKeys(rejection, "name", "message", "code", "details")
                    && rejection.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString()!.Length > 0
                    && rejection.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    var code = rejection.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                        ? codeValue.GetString()
                        : null;
                    var details = rejection.TryGetProperty("details", out var detailsValue) ? detailsValue : (JsonElement?)null;
                    outcome = new RemoteEventOutcome(RemoteEventOutcomeKind.Rejected,
                        Error: new RemoteRejectionException(name.GetString()!, message.GetString()!, code, details));
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool HasOnlyKey(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out _)) return false;
        var count = 0;
        foreach (var _ in element.EnumerateObject()) count++;
        return count == 1;
    }

    private static bool HasOnlyKeys(JsonElement element, string required, params string[] optional)
    {
        var count = 0;
        var foundRequired = false;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (property.Name == required) foundRequired = true;
            else if (!optional.Contains(property.Name, StringComparer.Ordinal)) return false;
        }
        return count >= 1 && foundRequired;
    }
}

/// <summary>
/// The restored client rejection (port of the TS <c>restoreRemoteEventRejection</c>): the remote
/// listener's name, message, code, and JSON-safe details survive the wire.
/// </summary>
public sealed class RemoteRejectionException : Exception
{
    /// <summary>Create the restored rejection.</summary>
    public RemoteRejectionException(string name, string message, string? code, JsonElement? details)
        : base(message)
    {
        Name = name;
        Code = code;
        Details = details;
    }

    /// <summary>The rejecting listener's error name.</summary>
    public string Name { get; }

    /// <summary>The optional stable machine code.</summary>
    public string? Code { get; }

    /// <summary>The optional JSON-safe details.</summary>
    public JsonElement? Details { get; }
}
