using System.Text.RegularExpressions;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.SessionQuery;

/// <summary>
/// ctx.sessionQuery: the log fold provider. Every query folds Session.Events directly — there is
/// no index or corpus; the fold is the port of the TS service's backend-independent concrete
/// behavior. The surface filter derives membership from the event vocabulary: message-producing
/// events are "current" (the C# surface has no replace op yet, so "shadowed" never occurs) and
/// everything else is "log-only".
/// </summary>
public sealed class LogSessionQueryProvider : Service, ISessionQueryService
{
    /// <summary>Create and register the service as <c>sessionQuery</c>.</summary>
    /// <param name="ctx">the owner context.</param>
    public LogSessionQueryProvider(Context ctx)
        : base(ctx, "sessionQuery")
    {
    }

    /// <summary>Read the session-query service from a context, failing explicitly when it is absent.</summary>
    public static LogSessionQueryProvider Require(Context ctx) => ctx.Require<LogSessionQueryProvider>("sessionQuery");

    /// <inheritdoc />
    public IReadOnlyList<SessionEvent> EventsByType(Dsh.Session.Session session, string type)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(type);
        return session.Events.Where(evt => evt.Type == type).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionEventDocument> FilterEvents(Dsh.Session.Session session, IReadOnlyList<SessionEventFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(filters);
        var predicates = filters.Select(Compile).ToArray();
        var documents = new List<SessionEventDocument>();
        foreach (var evt in session.Events)
        {
            var document = BuildDocument(evt);
            if (predicates.All(predicate => predicate(document))) documents.Add(document);
        }
        return documents;
    }

    /// <inheritdoc />
    public IReadOnlyList<TurnRecord> Turns(Dsh.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var turns = new List<TurnRecord>();
        foreach (var evt in session.Events)
        {
            switch (evt)
            {
                case TurnStartEvent start:
                    turns.Add(new TurnRecord(start.Turn, start.Seq, null));
                    break;
                case TurnEndEvent end:
                    var open = turns.LastOrDefault(turn => turn.Turn == end.Turn && turn.EndSeq is null);
                    if (open is null) continue; // a restored log can begin mid-turn
                    turns[turns.IndexOf(open)] = open with { EndSeq = end.Seq };
                    break;
            }
        }
        return turns;
    }

    /// <inheritdoc />
    public IReadOnlyList<Message> Messages(Dsh.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.DeriveMessages();
    }

    /// <inheritdoc />
    public T Fold<T>(Dsh.Session.Session session, T seed, Func<T, SessionEvent, T> folder)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(folder);
        var state = seed;
        foreach (var evt in session.Events) state = folder(state, evt);
        return state;
    }

    /// <inheritdoc />
    public string ExtractEventText(SessionEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return EventTextExtraction.ExtractEventText(evt);
    }

    private static SessionEventDocument BuildDocument(SessionEvent evt)
    {
        var surface = Surface.IsSurfaceEligibleType(evt)
            ? SessionEventSurfaces.Current
            : SessionEventSurfaces.LogOnly;
        return new SessionEventDocument(evt.Seq, evt.TimeMs, evt.Type, surface, EventTextExtraction.ExtractEventText(evt));
    }

    private static Func<SessionEventDocument, bool> Compile(SessionEventFilter filter)
    {
        switch (filter)
        {
            case SeqRangeFilter seq:
                var seqRange = ValidateRange("seq", seq.Range);
                return document => MatchesRange(document.Seq, seqRange);
            case TimeRangeFilter time:
                var timeRange = ValidateRange("time", time.Range);
                return document => MatchesRange(document.TimeMs, timeRange);
            case TypeFilter type:
                ValidateStrings("type", type.Values);
                return document => type.Values.Contains(document.Type);
            case SurfaceFilter surface:
                ValidateSurfaceValues(surface.Values);
                return document => surface.Values.Contains(document.Surface);
            case TextFilter text:
                return CompileText(text.Text);
            default:
                throw InvalidFilter($"unknown filter kind \"{filter.Kind}\"");
        }
    }

    private static SessionResultRange ValidateRange(string name, SessionResultRange range)
    {
        if (range.From is { } from && from < 0)
        {
            throw InvalidFilter($"{name} filter from must be a non-negative integer");
        }
        if (range.To is { } to && to < 0)
        {
            throw InvalidFilter($"{name} filter to must be a non-negative integer");
        }
        if (range.From is { } lower && range.To is { } upper && lower > upper)
        {
            throw InvalidFilter($"{name} filter from must be less than or equal to to");
        }
        return range;
    }

    private static void ValidateStrings(string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0 || values.Any(value => value.Length == 0))
        {
            throw InvalidFilter($"{name} filter values must be a non-empty array of non-empty strings");
        }
    }

    private static void ValidateSurfaceValues(IReadOnlyList<string> values)
    {
        var allowed = new[] { SessionEventSurfaces.Current, SessionEventSurfaces.Shadowed, SessionEventSurfaces.LogOnly };
        if (values.Count == 0 || values.Any(value => !allowed.Contains(value)))
        {
            throw InvalidFilter($"surface filter contains an unknown value (expected current, shadowed, or log-only)");
        }
    }

    /// <summary>Compile a literal case-insensitive, whitespace-flexible semantic-text match.</summary>
    private static Func<SessionEventDocument, bool> CompileText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw InvalidFilter("text filter must contain non-whitespace text");
        }
        var pattern = string.Join(@"\s+", trimmed
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Regex.Escape(part)));
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return document => regex.IsMatch(document.Text);
    }

    private static bool MatchesRange(long value, SessionResultRange range)
        => (range.From is null || value >= range.From) && (range.To is null || value <= range.To);

    private static SessionQueryError InvalidFilter(string detail)
        => new($"session {detail}", SessionQueryErrorCodes.InvalidFilter);
}
