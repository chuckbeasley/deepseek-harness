using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Harness.Web.Host;

/// <summary>
/// The auth-fence middleware (the TS request fence applied to every Host surface): the index path
/// goes through <see cref="WebAuthFence.AuthorizeIndex"/> (token exchange or cookie), every API
/// surface — the gateway <c>/api</c>, the mux, the hub, and the Blazor circuit — through the trust
/// fence (403) then the browser-session cookie (401). Static assets stay open: they carry no
/// secrets, matching the TS where only the index and the API surfaces are gated.
/// </summary>
public static class WebAuthFenceMiddleware
{
    /// <summary>Register the fence in the pipeline, before the gateway, mux, hub, and app mapping.</summary>
    public static void UseFence(this WebApplication app, WebAuthFence fence, string hubPath)
    {
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path.Value ?? string.Empty;
            if (path == "/")
            {
                if (!await fence.AuthorizeIndex(http)) return;
                await next();
                return;
            }
            if (IsApiPath(path, hubPath))
            {
                if (!fence.IsTrustedRequest(http))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsync("forbidden");
                    return;
                }
                if (!fence.IsAuthenticated(http))
                {
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await http.Response.WriteAsync("unauthorized");
                    return;
                }
            }
            await next();
        });
    }

    /// <summary>Whether a request path is an API surface the fence gates.</summary>
    private static bool IsApiPath(string path, string hubPath)
        => MatchesPrefix(path, "/api")
            || MatchesPrefix(path, hubPath)
            || MatchesPrefix(path, "/_blazor");

    private static bool MatchesPrefix(string path, string prefix)
        => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
}
