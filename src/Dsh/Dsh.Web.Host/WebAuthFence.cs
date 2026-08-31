using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Dsh.Web.Host;

/// <summary>
/// The loopback process-token fence (port of the TS browser-auth + api-request-trust pair): the
/// Host/Origin trust fence over every API surface and the launch-token exchange that mints an
/// authority-bound signed browser cookie. The fence shape matches the TS: 403 for an untrusted
/// Host/Origin, 401 for a missing or invalid browser session, and index authorization through the
/// process-token exchange or the persistent cookie. This loopback variant keeps the signing
/// secret per host instance â€” the TS persists it in a credential record so cookies survive host
/// restarts; here a restart invalidates every cookie and the operator reopens the URL printed by
/// <c>dsh web</c> (documented reduction).
/// </summary>
public sealed class WebAuthFence
{
    /// <summary>The launch-token query parameter (the TS constant).</summary>
    public const string TokenQuery = "token";

    /// <summary>Cookie-name prefix (the TS constant).</summary>
    public const string CookiePrefix = "dsh-auth-";

    /// <summary>Random-material size in bytes for the launch token and the signing secret.</summary>
    public const int SecretBytes = 32;

    /// <summary>Absolute browser-cookie lifetime (the TS default).</summary>
    public static readonly TimeSpan DefaultCookieMaxAge = TimeSpan.FromDays(1);

    private const int CookiePayloadVersion = 1;
    private const string CookieValueVersion = "v1";
    private const string UnauthorizedIndexText = "dsh web authentication required; reopen the URL printed by dsh web.\n";

    private readonly byte[] _signingSecret;
    private readonly TimeSpan _maxAge;

    /// <summary>Create the fence with a fresh launch token and a fresh signing secret.</summary>
    public WebAuthFence(byte[]? signingSecret = null, TimeSpan? cookieMaxAge = null)
    {
        LaunchToken = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        _signingSecret = signingSecret ?? RandomNumberGenerator.GetBytes(SecretBytes);
        _maxAge = cookieMaxAge ?? DefaultCookieMaxAge;
    }

    /// <summary>This host instance's launch token; the operator opens the URL carrying it once.</summary>
    public string LaunchToken { get; }

    /// <summary>The application root URL carrying the launch token as its sole authentication input.</summary>
    public string AuthenticatedUrl(string origin) => $"{origin.TrimEnd('/')}/?{TokenQuery}={LaunchToken}";

    /// <summary>
    /// The Host/Origin trust fence (the TS <c>isTrustedApiRequest</c>, loopback only): the Host
    /// must name the loopback authority, an explicit cross-site fetch marker is refused, and a
    /// present Origin must equal the Host authority. No trustedHosts deployment authorities are
    /// ported: the loopback binding is the Wave-1 surface (documented reduction).
    /// </summary>
    public bool IsTrustedRequest(HttpContext http)
    {
        var host = http.Request.Host;
        if (host.Host.Length == 0 || !IsLoopbackHostname(host.Host)) return false;
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
