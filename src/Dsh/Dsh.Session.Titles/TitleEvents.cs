using System.Text.Json.Serialization;
using Dsh.Session;

namespace Dsh.Session.Titles;

/// <summary>Who supplied an accepted session title (port of the TS SessionTitleSource).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FallbackTitleSource), "fallback")]
[JsonDerivedType(typeof(ProviderTitleSource), "provider")]
[JsonDerivedType(typeof(UserTitleSource), "user")]
public abstract record SessionTitleSource;

/// <summary>The built-in first-prompt fallback supplied the title.</summary>
public sealed record FallbackTitleSource : SessionTitleSource;

/// <summary>A registered provider supplied the title.</summary>
public sealed record ProviderTitleSource(string Provider, string? Model = null) : SessionTitleSource;

/// <summary>An explicit user rename supplied the title.</summary>
public sealed record UserTitleSource : SessionTitleSource;

/// <summary>
/// The session's accepted title (port of the TS <c>session/title</c>): log-only, durable and
/// replayable. The latest event is the session's title; a fallback or provider event cites the
/// exact human <c>user/message</c> seqs it derived from, a user rename cites none.
/// </summary>
public sealed record SessionTitleEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "session/title";

    /// <summary>Normalized non-empty title text.</summary>
    public required string Title { get; init; }

    /// <summary>Exact human <c>user/message</c> seqs used to derive the title; empty for an explicit user rename.</summary>
    public required IReadOnlyList<long> MessageSeqs { get; init; }

    /// <summary>Whether the fallback, a provider, or the user supplied the title.</summary>
    public required SessionTitleSource Source { get; init; }

    /// <inheritdoc />
    public override string Type => EventTypeName;
}

/// <summary>Register the title event type (idempotent per discriminator).</summary>
public static class TitleEventTypes
{
    /// <summary>Register the session-title marker.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(SessionTitleEvent.EventTypeName, typeof(SessionTitleEvent));
    }
}