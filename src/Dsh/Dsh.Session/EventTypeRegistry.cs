using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dsh.Session;

/// <summary>
/// Registry of plugin-merged session event types beyond the spine vocabulary (port of the TS
/// session event-type registry). The spine's own event types are declared on
/// <see cref="SessionEvent"/>; plugins register their additional event types at boot under the
/// event's wire discriminator (its <c>Type</c> string) so the JSONL backend can serialize and
/// replay them polymorphically. Registration is append-only and idempotent per discriminator.
/// </summary>
public static class SessionEventTypes
{
    private static readonly object Gate = new();
    private static readonly List<KeyValuePair<string, Type>> Extra = new();

    /// <summary>
    /// Register one plugin-merged session event type. A later registration for the same
    /// discriminator is ignored (the first registration wins).
    /// </summary>
    /// <param name="discriminator">the event's wire discriminator (its <c>Type</c> string).</param>
    /// <param name="eventType">the event record type; must derive from <see cref="SessionEvent"/>.</param>
    public static void Register(string discriminator, Type eventType)
    {
        ArgumentNullException.ThrowIfNull(discriminator);
        ArgumentNullException.ThrowIfNull(eventType);
        if (!typeof(SessionEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException($"event type {eventType} must derive from {nameof(SessionEvent)}", nameof(eventType));
        }
        lock (Gate)
        {
            if (Extra.Any(entry => entry.Key == discriminator)) return;
            Extra.Add(new KeyValuePair<string, Type>(discriminator, eventType));
        }
    }

    /// <summary>
    /// JSON options for the session log: the spine's declared polymorphic handling plus every
    /// currently registered plugin-merged event type.
    /// </summary>
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        KeyValuePair<string, Type>[] extra;
        lock (Gate) extra = Extra.ToArray();
        var resolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                typeInfo =>
                {
                    if (typeInfo.Type != typeof(SessionEvent)) return;
                    foreach (var (discriminator, eventType) in extra)
                    {
                        typeInfo.PolymorphismOptions!.DerivedTypes.Add(new JsonDerivedType(eventType, discriminator));
                    }
                },
            },
        };
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }
}
