using System.Text.Json;
using System.Threading.Channels;
using Harness.Cordis.Core;
using Harness.Session;

namespace Harness.Web.Host;

/// <summary>
/// The session remote methods (port of the session-controller slice this wave ports): the unary
/// history page and the live follow stream. The address vocabulary supports the session kind;
/// subagent addresses are refused with <c>gateway/bad-request</c> until the subagent catalog wave.
/// </summary>
public static class SessionRemotes
{
    /// <summary>The default follow/page record window when the caller names none.</summary>
    public const int DefaultMaxMessages = 200;

    /// <summary>The unary history page: records up to <c>throughSeq</c>, windowed by <c>beforeSeq</c>/<c>maxMessages</c>.</summary>
    public static RpcMethod Page(Context ctx, SessionStore sessions)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(sessions);
        return new RpcMethod("session/page", (args, _) =>
        {
            var (session, throughSeq, beforeSeq, maxMessages) = ResolvePageArgs(sessions, args);
            var records = session.Events
                .Where(evt => evt.Seq <= throughSeq && (beforeSeq is null || evt.Seq < beforeSeq))
                .TakeLast(maxMessages ?? DefaultMaxMessages)
                .Select(evt => JsonSerializer.SerializeToElement(new { type = "event", @event = SessionWireEvent.Project(evt) }))
                .ToArray();
            var hasMore = session.Events.Count(evt => evt.Seq <= throughSeq && (beforeSeq is null || evt.Seq < beforeSeq)) > records.Length;
            return Task.FromResult<JsonElement?>(
                JsonSerializer.SerializeToElement(new { records, hasMore }));
        });
    }

    /// <summary>
    /// The live follow stream: one snapshot frame (header, cursor, records, hasMore, projections),
    /// then one <c>event</c> frame per live event with gap-free sequence checks.
    /// </summary>
    public static RpcStreamMethod Follow(Context ctx, SessionStore sessions)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(sessions);
        return new RpcStreamMethod("session/follow", (args, ct) => FollowAsync(ctx, sessions, args, ct));
    }

    private static async IAsyncEnumerable<JsonElement> FollowAsync(
        Context ctx, SessionStore sessions, JsonElement? args, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var session = ResolveAddress(sessions, args);
        var maxMessages = MaxMessagesOf(args);
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var cursor = session.Events.LastOrDefault()?.Seq ?? 0;
        var records = session.Events.TakeLast(maxMessages ?? DefaultMaxMessages)
            .Select(SessionWireEvent.Project)
            .ToArray();
        channel.Writer.TryWrite(JsonSerializer.SerializeToElement(new
        {
            type = "snapshot",
            header = new { sessionId = session.Id.Value, title = "" },
            cursor,
            records,
            hasMore = false,
            projections = new { asOfSeq = cursor, values = new { } },
        }));
        // The C# session log assigns seq = log length at append (0-based), so the next expected
        // seq after a snapshot is the log count, not cursor + 1.
        long expected = session.Events.Count;
        using var subscription = ctx.On("session/event",
            new Action<Harness.Session.Session, SessionEvent>((liveSession, evt) =>
            {
                if (liveSession.Id != session.Id) return;
                if (evt.Seq != expected)
                {
                    channel.Writer.TryWrite(JsonSerializer.SerializeToElement(new
                    {
                        type = "error",
                        error = new { code = RpcErrorCodes.Internal, message = $"skipped seq: expected {expected}, got {evt.Seq}", details = new { } },
                    }));
                    return;
                }
                expected = evt.Seq + 1;
                channel.Writer.TryWrite(JsonSerializer.SerializeToElement(new
                {
                    type = "event",
                    @event = SessionWireEvent.Project(evt),
                }));
            }));
        // Cancellation completes the channel, so the token-free read ends normally and the
        // stream ends quietly without a terminal frame (the TS cancel contract).
        using var cancel = ct.Register(() => channel.Writer.TryComplete());
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync())
            {
                yield return frame;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            subscription.Dispose();
        }
    }

    /// <summary>Resolve the session-kind address and the windowing args, failing loud on bad requests.</summary>
    private static (Harness.Session.Session Session, long ThroughSeq, long? BeforeSeq, int? MaxMessages) ResolvePageArgs(SessionStore sessions, JsonElement? args)
    {
        var session = ResolveAddress(sessions, args);
        var throughSeq = LongArg(args, "throughSeq")
            ?? throw new RpcBadRequestException("session/page requires a non-negative throughSeq");
        var beforeSeq = LongArg(args, "beforeSeq");
        if (beforeSeq is long before && before <= 0)
        {
            throw new RpcBadRequestException("session/page beforeSeq must be positive");
        }
        var maxMessages = MaxMessagesOf(args);
        return (session, throughSeq, beforeSeq, maxMessages);
    }

    /// <summary>Resolve one address object; only the session kind is ported this wave.</summary>
    private static Harness.Session.Session ResolveAddress(SessionStore sessions, JsonElement? args)
    {
        var address = args is JsonElement element && element.TryGetProperty("address", out var addressValue)
            ? addressValue
            : default;
        if (address.ValueKind != JsonValueKind.Object)
        {
            throw new RpcBadRequestException("session methods require an address object");
        }
        var kind = address.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
        if (kind != "session")
        {
            throw new RpcBadRequestException($"address kind \"{kind}\" is not ported yet (session kind only)");
        }
        var sessionId = address.TryGetProperty("sessionId", out var idValue) ? idValue.GetString() : null;
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new RpcBadRequestException("session address requires sessionId");
        }
        return sessions.Get(new SessionId(sessionId))
            ?? throw new RpcSessionNotFoundError(sessionId);
    }

    private static long? LongArg(JsonElement? args, string key)
        => args is JsonElement element
            && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private static int? MaxMessagesOf(JsonElement? args)
    {
        var value = LongArg(args, "maxMessages");
        if (value is long count && (count <= 0 || count > 10000))
        {
            throw new RpcBadRequestException("session maxMessages must be between 1 and 10000");
        }
        return value is null ? null : (int)value.Value;
    }
}

/// <summary>The domain failure for a live session that is not in the store.</summary>
public sealed class RpcSessionNotFoundError : Exception
{
    /// <summary>Create the failure naming the absent session id.</summary>
    public RpcSessionNotFoundError(string sessionId)
        : base($"session \"{sessionId}\" is not live")
    {
    }
}

