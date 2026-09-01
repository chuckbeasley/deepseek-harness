using Harness.Llm;
using Harness.Session;

namespace Harness.SessionQuery;

/// <summary>
/// Session-query Service Definition (ctx.sessionQuery): combined session-history reads — event-type
/// filters, turn enumeration, derived messages, and generic folds — over a session log. Port of
/// <c>@deepseek-ai/hsh-session-query</c>'s SessionQueryEngine reduced to the in-memory fold over
/// Session.Events. Full-text search, corpus listing, titles, tracing, cursor paging, and the
/// sqlite backend are deferred (named, not ported: session-query-sqlite, session-log-export,
/// tool-session-query).
/// </summary>
public interface ISessionQueryService
{
    /// <summary>Events of one type, in log order.</summary>
    IReadOnlyList<SessionEvent> EventsByType(Harness.Session.Session session, string type);

    /// <summary>Semantic documents accepted by every ANDed filter, in log order.</summary>
    /// <exception cref="SessionQueryError">with <see cref="SessionQueryErrorCodes.InvalidFilter"/> for an invalid filter.</exception>
    IReadOnlyList<SessionEventDocument> FilterEvents(Harness.Session.Session session, IReadOnlyList<SessionEventFilter> filters);

    /// <summary>Turns folded from turn/start and turn/end events, in open order; an open turn has a null EndSeq.</summary>
    IReadOnlyList<TurnRecord> Turns(Harness.Session.Session session);

    /// <summary>The session's derived model-visible messages (the surface fold).</summary>
    IReadOnlyList<Message> Messages(Harness.Session.Session session);

    /// <summary>Generic fold over the session log.</summary>
    T Fold<T>(Harness.Session.Session session, T seed, Func<T, SessionEvent, T> folder);

    /// <summary>First-party semantic text for one event; an empty string when non-searchable.</summary>
    string ExtractEventText(SessionEvent evt);
}
