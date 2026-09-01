using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Harness.Web.Host;

/// <summary>
/// The loopback process-token fence (port of the TS browser-auth + api-request-trust pair): the
/// Host/Origin trust fence over every API surface and the launch-token exchange that mints an
/// authority-bound signed browser cookie. The fence shape matches the TS: 403 for an untrusted
/// Host/Origin, 401 for a missing or invalid browser session, and index authorization through the
/// process-token exchange or the persistent cookie. The signing secret is per host instance when
/// no credentials seam is composed; when one is, the host resolves (or creates) a
/// <c>HSH_WEB_SESSION_SECRET</c> credential so cookies survive host restarts like the TS
/// credential record (see <see cref="WebHostService"/>).
/// </summary>
public sealed class WebAuthFence
{
    /// <summary>The launch-token query parameter (the TS constant).</summary>
    public const string TokenQuery = "token";

    /// <summary>Cookie-name prefix (the TS constant).</summary>
    public const string CookiePrefix = "hsh-auth-";

    /// <summary>Random-material size in bytes for the launch token and the signing secret.</summary>
    public const int SecretBytes = 32;

    /// <summary>Absolute browser-cookie lifetime (the TS default).</summary>
    public static readonly TimeSpan DefaultCookieMaxAge = TimeSpan.FromDays(1);

    private const int CookiePayloadVersion = 1;
    private const string CookieValueVersion = "v1";
    private const string UnauthorizedIndexText = "hsh web authentication required; reopen the URL printed by hsh web.\n";

    private readonly byte[] _signingSecret;
    private readonly TimeSpan _maxAge;
    private readonly IReadOnlyList<string> _trustedHosts;

    /// <summary>Create the fence with a fresh launch token and a fresh signing secret.</summary>
    public WebAuthFence(byte[]? signingSecret = null, TimeSpan? cookieMaxAge = null, IReadOnlyList<string>? trustedHosts = null)
    {
        LaunchToken = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        _signingSecret = signingSecret ?? RandomNumberGenerator.GetBytes(SecretBytes);
        _maxAge = cookieMaxAge ?? DefaultCookieMaxAge;
        _trustedHosts = trustedHosts ?? Array.Empty<string>();
    }

    /// <summary>This host instance's launch token; the operator opens the URL carrying it once.</summary>
    public string LaunchToken { get; }

    /// <summary>The application root URL carrying the launch token as its sole authentication input.</summary>
    public string AuthenticatedUrl(string origin) => $"{origin.TrimEnd('/')}/?{TokenQuery}={LaunchToken}";

    /// <summary>Parse one stored signing secret: exactly <see cref="SecretBytes"/> decoded bytes, or <c>null</c>.</summary>
    public static byte[]? DecodeSecret(string value)
        => DecodeBase64Url(value) is { Length: SecretBytes } bytes ? bytes : null;

    /// <summary>
    /// The Host/Origin trust fence (the TS <c>isTrustedApiRequest</c>): the Host must name the
    /// loopback authority or a declared <c>trustedHosts</c> authority, an explicit cross-site
    /// fetch marker is refused, and a present Origin must equal the Host authority. A
    /// <c>trustedHosts</c> entry matches the WHATWG way: a port-less entry trusts the hostname on
    /// any port (the LAN-serving shape), an explicit entry trusts the exact authority with the
    /// http default port dropped on both sides. Configured entries are asserted at host start
    /// (see <see cref="AssertTrustedAuthority"/>), so a malformed grant fails the boot loudly.
    /// </summary>
    public bool IsTrustedRequest(HttpContext http)
    {
        var host = http.Request.Host;
        if (host.Host.Length == 0) return false;
        if (!IsLoopbackHostname(host.Host) && !IsTrustedAuthority(host.Host, host.Port, _trustedHosts)) return false;
        if (string.Equals(http.Request.Headers["sec-fetch-site"].FirstOrDefault(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var origin = http.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin)) return true;
        if (origin == "null") return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && originUri.Scheme is "http" or "https"
            && Uri.TryCreate($"http://{http.Request.Host}", UriKind.Absolute, out var hostUri)
            && string.Equals(originUri.Authority, hostUri.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assert one configured <c>trustedHosts</c> entry is a bare authority (<c>host</c> or
    /// <c>host:port</c>) in canonical form — it must survive parsing unchanged, case aside (the
    /// TS <c>assertTrustedAuthority</c>). Anything parsing would silently rewrite is refused as
    /// a typo that must fail the load loudly instead of being ignored until requests 403 or
    /// quietly changing the grant: URL parts beyond the authority (a path, userinfo), stripped
    /// whitespace, a dangling or zero-padded port, and non-canonical host spellings. The one
    /// documented divergence from WHATWG: numeric dotted literals are validated as canonical
    /// IPv4, but hex forms like <c>0x7f.0.0.1</c> are accepted as hostnames (the .NET Uri never
    /// normalizes them), where the TS rejects them through WHATWG normalization.
    /// </summary>
    /// <param name="entry">the configured value, verbatim.</param>
    /// <exception cref="ArgumentException">when the entry is not a bare authority.</exception>
    public static void AssertTrustedAuthority(string entry)
    {
        var canonical = CanonicalAuthority(entry);
        if (canonical is null || !string.Equals(canonical, entry.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"trustedHosts entry \"{entry}\" is not a bare host[:port] authority");
        }
    }

    /// <summary>
    /// Canonical form of a parsed authority: <c>hostname</c> when no port was written, else
    /// <c>hostname:port</c>, with the IPv6 literal bracketed and the hostname lowercased; null
    /// when the entry is not a bare authority. A zero-padded port is refused up front (it would
    /// broaden an intended exact-port grant to every port after normalization).
    /// </summary>
    private static string? CanonicalAuthority(string entry)
    {
        var colon = PortColon(entry);
        if (colon >= 0)
        {
            var port = entry[(colon + 1)..];
            if (port.Length > 1 && port[0] == '0') return null;
        }
        if (!TryParseAuthority(entry, out var hostname, out var portValue)) return null;
        if (IsNumericDotted(hostname) && !IsCanonicalIPv4(hostname)) return null;
        return portValue is null ? hostname : $"{hostname}:{portValue}";
    }

    /// <summary>Whether the request authority matches a <c>trustedHosts</c> entry (the TS matching rules).</summary>
    private static bool IsTrustedAuthority(string hostname, int? port, IReadOnlyList<string> trustedHosts)
    {
        var requestHost = CanonicalHostname(hostname);
        foreach (var entry in trustedHosts)
        {
            if (!TryParseAuthority(entry, out var entryHost, out var entryPort)) continue;
            if (!string.Equals(entryHost, requestHost, StringComparison.OrdinalIgnoreCase)) continue;
            // A port-less entry matches the hostname on any port (the CLI-derived LAN shape).
            if (entryPort is null) return true;
            // An explicit entry compares WHATWG hosts: the http default port (80) is dropped on
            // both sides, so an explicit :80 entry equals any request on the default port.
            var entryHostForm = entryPort == "80" ? entryHost : $"{entryHost}:{entryPort}";
            var requestHostForm = port is null or 80 ? requestHost : $"{requestHost}:{port}";
            if (string.Equals(entryHostForm, requestHostForm, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Parse a Host-header authority into its hostname and explicit port, or false when it is not
    /// a bare authority. The port is read from the raw string (the .NET Uri drops the scheme's
    /// default port from <see cref="Uri.Authority"/>, but an explicit <c>:80</c> still counts as
    /// explicit — the TS judges the port from both special-scheme parses) and must be numeric.
    /// </summary>
    private static bool TryParseAuthority(string authority, out string hostname, out string? port)
    {
        hostname = "";
        port = null;
        if (authority.Length == 0 || !Uri.TryCreate("http://" + authority, UriKind.Absolute, out var uri)) return false;
        if (uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        hostname = CanonicalHostname(uri.Host);
        var colon = PortColon(authority);
        if (colon >= 0)
        {
            port = authority[(colon + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit)) return false;
        }
        return true;
    }

    /// <summary>Bracket an unbracketed IPv6 literal so both sides of a comparison share one form.</summary>
    private static string CanonicalHostname(string hostname)
        => hostname.IndexOf(':') >= 0 && !hostname.StartsWith('[') ? $"[{hostname}]" : hostname;

    /// <summary>Index of the authority's port colon (outside an IPv6 bracket group), or -1.</summary>
    private static int PortColon(string entry)
    {
        var start = entry.StartsWith('[') ? entry.IndexOf(']') : -1;
        return entry.IndexOf(':', start + 1);
    }

    /// <summary>Whether a hostname consists only of digits and dots (a numeric dotted literal).</summary>
    private static bool IsNumericDotted(string hostname)
    {
        foreach (var ch in hostname)
        {
            if (!char.IsAsciiDigit(ch) && ch != '.') return false;
        }
        return true;
    }

    /// <summary>Whether a numeric dotted literal is canonical IPv4: four parts, each 1-3 digits, no leading zeros, at most 255.</summary>
    private static bool IsCanonicalIPv4(string hostname)
    {
        var parts = hostname.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (part.Length > 1 && part[0] == '0') return false;
            if (!int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var value) || value > 255) return false;
        }
        return true;
    }

    /// <summary>
    /// Verify the authority-bound browser cookie on one request: unexpired, signed by this
    /// instance's secret, and naming exactly the request authority.
    /// </summary>
    public bool IsAuthenticated(HttpContext http)
    {
        var authority = RequestAuthority(http);
        if (authority is null) return false;
        var value = CookieValue(http.Request.Headers.Cookie.ToString(), CookieName(authority));
        if (value is null) return false;
        var payload = DecodeCookie(value);
        if (payload is null || payload.Value.Authority != authority) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return payload.Value.IssuedAt <= now
            && payload.Value.ExpiresAt > now
            && payload.Value.ExpiresAt > payload.Value.IssuedAt
            && payload.Value.ExpiresAt - payload.Value.IssuedAt <= (long)_maxAge.TotalMilliseconds;
    }

    /// <summary>
    /// Authenticate an index request: a valid root token mints the cookie and redirects to clean
    /// <c>/</c>; a valid cookie lets the caller serve the index; every other request receives the
    /// same minimal 401 response.
    /// </summary>
    /// <returns>true only when the caller may serve the index.</returns>
    public async Task<bool> AuthorizeIndex(HttpContext http)
    {
        var tokens = http.Request.Query[TokenQuery];
        if (tokens.Count > 0)
        {
            var authority = RequestAuthority(http);
            if (http.Request.Method == HttpMethods.Get
                && tokens.Count == 1
                && authority is not null
                && TokenMatches(tokens[0]!, LaunchToken))
            {
                var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var expiresAt = issuedAt + (long)_maxAge.TotalMilliseconds;
                http.Response.StatusCode = StatusCodes.Status303SeeOther;
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.Location = "/";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                http.Response.Headers.SetCookie = SessionCookie(CookieName(authority), EncodeCookie(authority, issuedAt, expiresAt), expiresAt, _maxAge);
                return false;
            }
            if (IsAuthenticated(http))
            {
                http.Response.StatusCode = StatusCodes.Status303SeeOther;
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.Location = "/";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                return false;
            }
            await WriteUnauthorizedAsync(http);
            return false;
        }
        if (IsAuthenticated(http)) return true;
        await WriteUnauthorizedAsync(http);
        return false;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "text/plain; charset=utf-8";
        if (http.Request.Method != HttpMethods.Head)
        {
            await http.Response.WriteAsync(UnauthorizedIndexText);
        }
    }

    /// <summary>Canonical request authority (hostname plus non-default port), or null when unparsable.</summary>
    private static string? RequestAuthority(HttpContext http)
        => Uri.TryCreate($"http://{http.Request.Host}", UriKind.Absolute, out var uri) ? uri.Authority : null;

    /// <summary>Whether a normalized hostname names the loopback authority (the TS predicate).</summary>
    private static bool IsLoopbackHostname(string hostname)
    {
        if (hostname == "localhost" || hostname == "::1") return true;
        var parts = hostname.Split('.');
        return parts.Length == 4
            && parts[0] == "127"
            && parts.All(part => part.Length > 0 && part.Length <= 3 && part.All(char.IsAsciiDigit) && int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) <= 255);
    }

    private static string CookieName(string authority)
        => CookiePrefix + Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));

    private static string? CookieValue(string headerValue, string name)
    {
        foreach (var segment in headerValue.Split(';'))
        {
            var at = segment.IndexOf('=');
            if (at < 0 || !string.Equals(segment[..at].Trim(), name, StringComparison.Ordinal)) continue;
            return segment[(at + 1)..].Trim();
        }
        return null;
    }

    private static string SessionCookie(string name, string value, long expiresAt, TimeSpan maxAge)
        => $"{name}={value}; Max-Age={(long)maxAge.TotalSeconds}; Path=/; Expires={DateTimeOffset.FromUnixTimeMilliseconds(expiresAt).UtcDateTime.ToString("R")}; HttpOnly; SameSite=Strict";

    private string EncodeCookie(string authority, long issuedAt, long expiresAt)
    {
        var body = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = CookiePayloadVersion,
            authority,
            issuedAt,
            expiresAt,
        })));
        return $"{CookieValueVersion}.{body}.{Base64Url(Signature(body))}";
    }

    private (string Authority, long IssuedAt, long ExpiresAt)? DecodeCookie(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 3 || parts[0] != CookieValueVersion) return null;
        var expected = Signature(parts[1]);
        var actual = DecodeBase64Url(parts[2]);
        if (actual is null || !FixedTimeEquals(expected, actual)) return null;
        var bodyBytes = DecodeBase64Url(parts[1]);
        if (bodyBytes is null) return null;
        using var document = JsonDocument.Parse(bodyBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version) || version.GetInt32() != CookiePayloadVersion
            || !root.TryGetProperty("authority", out var authority) || authority.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("issuedAt", out var issuedAt) || !issuedAt.TryGetInt64(out var issuedAtValue)
            || !root.TryGetProperty("expiresAt", out var expiresAt) || !expiresAt.TryGetInt64(out var expiresAtValue))
        {
            return null;
        }
        return (authority.GetString()!, issuedAtValue, expiresAtValue);
    }

    private byte[] Signature(string body) => HMACSHA256.HashData(_signingSecret, Encoding.UTF8.GetBytes(body));

    private static bool TokenMatches(string actual, string expected)
        => FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    private static bool FixedTimeEquals(byte[] left, byte[] right)
        => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? DecodeBase64Url(string value)
    {
        if (value.Length % 4 == 1 || !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return null;
        var padding = value.Length % 4 == 0 ? 0 : 4 - value.Length % 4;
        try
        {
            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
