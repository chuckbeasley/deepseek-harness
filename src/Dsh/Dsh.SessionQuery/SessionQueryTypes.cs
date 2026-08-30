namespace Dsh.SessionQuery;

/// <summary>Stable machine codes for session-query failures (port of the TS SessionQueryErrorCode vocabulary).</summary>
public static class SessionQueryErrorCodes
{
    public const string InvalidFilter = "SESSION_QUERY_INVALID_FILTER";

    public const string InvalidConfig = "SESSION_QUERY_INVALID_CONFIG";

    public const string InvalidWindow = "SESSION_QUERY_INVALID_WINDOW";

    public const string EventNotFound = "SESSION_QUERY_EVENT_NOT_FOUND";
}

/// <summary>Loud failure of a session-query operation (port of SessionQueryError).</summary>
public sealed class SessionQueryError : Exception
{
    /// <summary>Create the error with a stable <paramref name="code"/> and an optional chained cause.</summary>
    public SessionQueryError(string message, string code, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="SessionQueryErrorCodes"/>).</summary>
    public string Code { get; }
}

/// <summary>Event surface-membership values for the surface filter (port of SessionEventSurface).</summary>
public static class SessionEventSurfaces
{
    public const string Current = "current";

    public const string Shadowed = "shadowed";

    public const string LogOnly = "log-only";
}

/// <summary>Searchable semantic document derived from one session event.</summary>
public sealed record SessionEventDocument(long Seq, long TimeMs, string Type, string Surface, string Text);

/// <summary>One inclusive interval with open bounds (port of SessionResultRange).</summary>
public sealed record SessionResultRange(long? From = null, long? To = null);

/// <summary>
/// One event predicate (port of SessionEventResultFilter). A filter array is ANDed; list-valued
/// clauses are ORed. Text is a literal, case-insensitive, whitespace-flexible semantic-text scan.
/// </summary>
public abstract record SessionEventFilter
{
    /// <summary>The clause discriminant: seq | time | type | surface | text.</summary>
    public abstract string Kind { get; }
}

/// <summary>Inclusive seq interval; omitted bounds are open.</summary>
public sealed record SeqRangeFilter(SessionResultRange Range) : SessionEventFilter
{
    public override string Kind => "seq";
}

/// <summary>Inclusive time (epoch ms) interval; omitted bounds are open.</summary>
public sealed record TimeRangeFilter(SessionResultRange Range) : SessionEventFilter
{
    public override string Kind => "time";
}

/// <summary>Event type values, ORed.</summary>
public sealed record TypeFilter(IReadOnlyList<string> Values) : SessionEventFilter
{
    public override string Kind => "type";
}

/// <summary>Surface membership values (current | shadowed | log-only), ORed.</summary>
public sealed record SurfaceFilter(IReadOnlyList<string> Values) : SessionEventFilter
{
    public override string Kind => "surface";
}

/// <summary>Literal case-insensitive, whitespace-flexible semantic-text scan.</summary>
public sealed record TextFilter(string Text) : SessionEventFilter
{
    public override string Kind => "text";
}

/// <summary>One turn of a session folded from turn/start + turn/end events; an open turn has a null EndSeq.</summary>
public sealed record TurnRecord(long Turn, long StartSeq, long? EndSeq);
