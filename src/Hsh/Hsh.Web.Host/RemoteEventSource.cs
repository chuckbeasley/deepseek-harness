using System.Text.Json;
using Harness.Cordis.Core;

namespace Harness.Web.Host;

/// <summary>One forwarded event dispatch: the event name and the JSON-safe argument list.</summary>
public sealed record RemoteEventDispatch(string Event, IReadOnlyList<JsonElement> Args);

/// <summary>
/// The forwarded-event source (the C# half of the TS <c>registerRemoteEvents</c> selection): the
/// port's own application-selected Cordis events, projected to JSON-safe emit arguments. The TS
/// allowlist is the TS app's selection; the port forwards its own emitted vocabulary (session
/// lifecycle, session events, agent status/error, tools change, authorization settlement) with
/// the same wire frame shapes.
/// </summary>
public static class RemoteEventSource
{
    /// <summary>The forwarded event selection, each with its typed listener wiring.</summary>
    private static readonly (string Event, Func<Context, Action<RemoteEventDispatch>, IDisposable> Wire)[] Selection =
    {
        ("session/created", (ctx, handler) => ctx.On<Harness.Session.Session>("session/created",
            session => handler(new RemoteEventDispatch("session/created", new[] { SessionJson(session) })))),
        ("session/disposed", (ctx, handler) => ctx.On<Harness.Session.Session>("session/disposed",
            session => handler(new RemoteEventDispatch("session/disposed", new[] { SessionJson(session) })))),
        ("session/event", (ctx, handler) => ctx.On("session/event",
            new Action<Harness.Session.Session, Harness.Session.SessionEvent>((session, evt) =>
                handler(new RemoteEventDispatch("session/event", new[] { SessionJson(session), EventJson(evt) }))))),
        ("agent/status", (ctx, handler) => ctx.On("agent/status",
            new Action<object>((payload) => handler(new RemoteEventDispatch("agent/status", new[] { Json(payload) }))))),
        ("agent/error", (ctx, handler) => ctx.On("agent/error",
            new Action<object>((payload) => handler(new RemoteEventDispatch("agent/error", new[] { Json(payload) }))))),
        ("tools/change", (ctx, handler) => ctx.On("tools/change",
            new Action(() => handler(new RemoteEventDispatch("tools/change", Array.Empty<JsonElement>()))))),
        ("authorization/settled", (ctx, handler) => ctx.On("authorization/settled",
            new Action<string, object>((key, settlement) =>
                handler(new RemoteEventDispatch("authorization/settled", new[] { Json(key), Json(settlement) }))))),
    };

    private static JsonSerializerOptions? _wireJson;
    private static int _wireJsonRevision = -1;

    /// <summary>The session-event serializer: the session log's polymorphic codecs, camel-cased for the wire; rebuilt when the event-type registry grows.</summary>
    private static JsonSerializerOptions WireJson()
    {
        var revision = Harness.Session.SessionEventTypes.Revision;
        if (_wireJson is null || _wireJsonRevision != revision)
        {
            _wireJson = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = Harness.Session.SessionEventTypes.CreateSerializerOptions().TypeInfoResolver,
            };
            _wireJsonRevision = revision;
        }
        return _wireJson;
    }

    /// <summary>
    /// Subscribe the selection. One subscription per event, contained per dispatch: a throwing
    /// projection drops the emit with a warning, never the event itself.
    /// </summary>
    public static IDisposable Subscribe(Context ctx, Action<RemoteEventDispatch> handler)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(handler);
        var disposers = new List<IDisposable>();
        foreach (var (eventName, wire) in Selection)
        {
            disposers.Add(wire(ctx, dispatch =>
            {
                try
                {
                    handler(dispatch);
                }
                catch (Exception error)
                {
                    ctx.Logger.Warn($"web: forwarding {eventName} failed: {error.Message}");
                }
            }));
        }
        return new CompositeDisposer(disposers);
    }

    private static JsonElement SessionJson(Harness.Session.Session session)
        => JsonSerializer.SerializeToElement(new { id = session.Id.Value }, WireJson());

    private static JsonElement EventJson(Harness.Session.SessionEvent evt)
        => JsonSerializer.SerializeToElement(evt, WireJson());

    private static JsonElement Json(object value)
        => JsonSerializer.SerializeToElement(value, WireJson());

    private sealed class CompositeDisposer : IDisposable
    {
        private readonly List<IDisposable> _disposers;

        public CompositeDisposer(List<IDisposable> disposers)
        {
            _disposers = disposers;
        }

        public void Dispose()
        {
            foreach (var disposer in _disposers) disposer.Dispose();
            _disposers.Clear();
        }
    }
}
