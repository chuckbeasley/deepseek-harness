using System.Text.Json;

namespace Dsh.Web.Host;

/// <summary>
/// The wire projection of one session event (port of the TS <c>SessionWireEvent</c>): the
/// envelope fields (<c>type</c>, <c>seq</c>, <c>time</c>) plus the event-specific data. The C#
/// log shape serializes the envelope inline; this projection lifts it out so the UI receives the
/// TS wire spelling.
/// </summary>
public static class SessionWireEvent
{
    private static JsonSerializerOptions? _sessionEventJson;
    private static int _sessionEventRevision = -1;

    /// <summary>
    /// The session log's polymorphic codecs, camel-cased for the wire. Rebuilt when the event-type
    /// registry gains a registration (the registry is append-only and plugins register at boot).
    /// </summary>
    private static JsonSerializerOptions SessionEventJson()
    {
        var revision = Dsh.Session.SessionEventTypes.Revision;
        if (_sessionEventJson is null || _sessionEventRevision != revision)
        {
            _sessionEventJson = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = Dsh.Session.SessionEventTypes.CreateSerializerOptions().TypeInfoResolver,
            };
            _sessionEventRevision = revision;
        }
        return _sessionEventJson;
    }

    /// <summary>Project one committed session event to its wire spelling.</summary>
    /// <param name="evt">the committed event.</param>
    /// <returns>the <c>{ type, seq, time, data }</c> wire object.</returns>
    public static JsonElement Project(Dsh.Session.SessionEvent evt)
    {
        var json = JsonSerializer.SerializeToElement(evt, SessionEventJson());
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
        {
            if (property.Name is "type" or "id" or "seq" or "timeMs" or "$type")
            {
                continue; // the envelope fields live at the wire top level, not in data
            }
            data[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(new
        {
            type = evt.Type,
            seq = evt.Seq,
            time = evt.TimeMs,
            data,
        }, SessionEventJson());
    }
}
