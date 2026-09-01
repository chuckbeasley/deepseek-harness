using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Llm;
using Harness.Session;

namespace Harness.Context;

/// <summary>
/// Canonical session-reference URI and mention encoding (port of session-reference/uri.ts):
/// <c>hsh-session:</c> URIs carry a base64url-encoded JSON string id; mentions render as
/// <c>@[label](uri)</c>. Decoding rejects non-canonical URIs exactly like the TS resolver.
/// </summary>
public static class SessionReferenceUri
{
    /// <summary>The URI scheme reserved for harness session snapshots.</summary>
    public const string Scheme = "hsh-session:";

    /// <summary>Encode any session-id string as a canonical lossless URI.</summary>
    public static string Encode(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var json = JsonSerializer.Serialize(sessionId);
        var bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return Scheme + bytes.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decode and canonicalize one session-reference URI.</summary>
    /// <exception cref="SessionReferenceError">with <see cref="SessionReferenceErrorCodes.InvalidReference"/> for a malformed or non-canonical URI.</exception>
    public static string Decode(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.StartsWith(Scheme, StringComparison.Ordinal))
        {
            throw new SessionReferenceError($"invalid session reference URI \"{uri}\"", SessionReferenceErrorCodes.InvalidReference);
        }
        var payload = uri[Scheme.Length..];
        if (payload.Length == 0 || !payload.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_'))
        {
            throw new SessionReferenceError($"invalid session reference URI \"{uri}\"", SessionReferenceErrorCodes.InvalidReference);
        }
        try
        {
            var padded = payload + new string('=', (4 - payload.Length % 4) % 4);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
            var sessionId = JsonSerializer.Deserialize<string>(json)
                ?? throw new InvalidOperationException("decoded session id is not a string");
            if (!Encode(sessionId).Equals(uri, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("URI is not canonical");
            }
            return sessionId;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
        {
            throw new SessionReferenceError($"invalid session reference URI \"{uri}\"", SessionReferenceErrorCodes.InvalidReference, error);
        }
    }
}

/// <summary>One extracted session reference and its display label.</summary>
public sealed record ParsedSessionReference(string SessionId, string Label);

/// <summary>
/// Mention extraction (port of parseSessionReferenceText): Markdown <c>@[label](hsh-session:...)</c>
/// mentions and bare canonical <c>hsh-session:</c> URIs, in appearance order.
/// </summary>
public static class SessionReferenceText
{
    private static readonly Regex MentionPattern = new(
        @"@\[((?:\\.|[^\\\]])*)\]\((hsh-session:[^\s)]*)\)|(hsh-session:[A-Za-z0-9_-]+)",
        RegexOptions.CultureInvariant);

    /// <summary>Extract structured references from one text value, in first-appearance order.</summary>
    /// <exception cref="SessionReferenceError">on any malformed or non-canonical URI.</exception>
    public static IReadOnlyList<ParsedSessionReference> ExtractReferences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var references = new List<ParsedSessionReference>();
        foreach (Match match in MentionPattern.Matches(text))
        {
            var uri = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            var sessionId = SessionReferenceUri.Decode(uri);
            var rawLabel = match.Groups[1].Success ? match.Groups[1].Value : null;
            var label = rawLabel is null ? sessionId : UnescapeLabel(rawLabel);
            references.Add(new ParsedSessionReference(sessionId, label));
        }
        return references;
    }

    private static string UnescapeLabel(string label) => Regex.Replace(label, @"\\(.)", "$1");
}

/// <summary>
/// Cross-session snapshot contributor (port of session-reference preparation): canonical
/// <c>hsh-session:</c> mentions in the session's user messages are resolved through the injected
/// session resolver and their recent derived messages contributed as background context. The TS
/// projection, byte-budget retention, and remote candidate surface are deferred (named, not
/// ported); self references and unresolvable sessions fail loud like the TS resolver.
/// </summary>
public sealed class SessionReferenceContributor : IContextContributor
{
    /// <summary>The contributor's stable key.</summary>
    public const string DefaultKey = "session-reference";

    private readonly Func<Harness.Session.SessionId, Harness.Session.Session?> _resolver;
    private readonly int _maxMessages;

    /// <summary>Create the contributor over a session resolver.</summary>
    /// <param name="resolver">maps a referenced session id to its live session; a null result fails loud.</param>
    /// <param name="maxMessages">maximum derived messages contributed per referenced session.</param>
    public SessionReferenceContributor(
        Func<Harness.Session.SessionId, Harness.Session.Session?> resolver,
        int maxMessages = 10)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _maxMessages = maxMessages > 0
            ? maxMessages
            : throw new ArgumentException("maxMessages must be positive", nameof(maxMessages));
    }

    /// <inheritdoc />
    public string Key => DefaultKey;

    /// <inheritdoc />
    public Task<ContextSection?> ContributeAsync(Harness.Session.Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var mentions = new List<ParsedSessionReference>();
        foreach (var evt in session.Events)
        {
            if (evt is not UserMessageEvent user) continue;
            foreach (var block in user.Message.Content)
            {
                if (block is TextBlock text) mentions.AddRange(SessionReferenceText.ExtractReferences(text.Text));
            }
        }
        if (mentions.Count == 0) return Task.FromResult<ContextSection?>(null);

        var sections = new List<string>();
        var seen = new HashSet<Harness.Session.SessionId>();
        foreach (var mention in mentions)
        {
            if (mention.SessionId == session.Id.Value)
            {
                throw new SessionReferenceError(
                    $"session \"{mention.SessionId}\" cannot reference itself",
                    SessionReferenceErrorCodes.SelfReference);
            }
            if (!seen.Add(new Harness.Session.SessionId(mention.SessionId))) continue;
            var referenced = _resolver(new Harness.Session.SessionId(mention.SessionId))
                ?? throw new SessionReferenceError(
                    $"failed to read referenced session \"{mention.SessionId}\"",
                    SessionReferenceErrorCodes.ReadFailed);
            sections.Add(RenderReferencedSession(mention, referenced));
        }
        if (sections.Count == 0) return Task.FromResult<ContextSection?>(null);
        return Task.FromResult<ContextSection?>(new ContextSection(Key, string.Join("\n\n", sections)));
    }

    private string RenderReferencedSession(ParsedSessionReference mention, Harness.Session.Session referenced)
    {
        var lines = referenced.DeriveMessages()
            .TakeLast(_maxMessages)
            .Select(message => $"{message.Role}: {RenderMessageText(message)}");
        return $"Referenced session: {mention.Label} ({mention.SessionId})\n\n{string.Join("\n", lines)}";
    }

    private static string RenderMessageText(Message message)
        => string.Join(" ", message.Content.OfType<TextBlock>().Select(block => block.Text));
}
