using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Credentials;
using Harness.Webhook;

namespace Harness.Webhook.Tests;

/// <summary>
/// Protocol behavior of <see cref="GitHubWebhookHandler"/>: method, content type, bounded body,
/// headers, signature verification, parsing, and dispatch — all over direct requests with no
/// sockets.
/// </summary>
public static class GitHubHandlerTests
{
    private const string Secret = "top-secret";

    private sealed class TempHome : IDisposable
    {
        public TempHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-webhook-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // already gone
            }
        }
    }

    private sealed class Spine : IDisposable
    {
        public required Context Ctx { get; init; }

        public required LocalCredentialsProvider Credentials { get; init; }

        public required WebhookRuntime Webhook { get; init; }

        public required GitHubWebhookHandler Handler { get; init; }

        public static Spine Create(int maxBodyBytes = 1024)
        {
            var ctx = new Context();
            var credentials = new LocalCredentialsProvider(ctx, new LocalCredentialsConfig(HshHome: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-webhook-creds-" + Guid.NewGuid().ToString("N"))));
            var webhook = new WebhookRuntime(ctx);
            var handler = new GitHubWebhookHandler(
                ctx,
                webhook,
                credentials,
                new GitHubWebhookHandlerConfig(new WebhookSourceId("primary-github"), "GITHUB_WEBHOOK_SECRET", maxBodyBytes));
            return new Spine { Ctx = ctx, Credentials = credentials, Webhook = webhook, Handler = handler };
        }

        public void Dispose() => Ctx.Dispose();
    }

    private static string Signature(string body) => "sha256=" + Convert.ToHexStringLower(SHA256Hash(Secret, body));

    private static byte[] SHA256Hash(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
    }

    private static WebhookHttpRequest ValidRequest(string body = "{\"ref\":\"refs/heads/main\"}", string? signature = null)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-hub-signature-256"] = new[] { signature ?? Signature(body) },
            ["x-github-delivery"] = new[] { "delivery-1" },
            ["x-github-event"] = new[] { "push" },
        };
        return new WebhookHttpRequest("POST", headers, Encoding.UTF8.GetBytes(body), "application/json");
    }

    public static void MethodNotPost_Answers405WithAllow()
    {
        using var spine = Spine.Create();
        var response = spine.Handler.HandleAsync(ValidRequest() with { Method = "GET" }).GetAwaiter().GetResult();
        Assert.Equal(405, response.Status);
        Assert.Equal("POST", response.Allow);
    }

    public static void ContentTypeMustBeJson()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", Secret).GetAwaiter().GetResult();
        var response = spine.Handler.HandleAsync(ValidRequest() with { ContentType = "text/plain" }).GetAwaiter().GetResult();
        Assert.Equal(415, response.Status);
        var withCharset = spine.Handler.HandleAsync(ValidRequest() with { ContentType = "application/json; charset=utf-8" }).GetAwaiter().GetResult();
        Assert.Equal(202, withCharset.Status, "a charset parameter is accepted and the request proceeds");
        var withBadCharset = spine.Handler.HandleAsync(ValidRequest() with { ContentType = "application/json; charset=iso-8859-1" }).GetAwaiter().GetResult();
        Assert.Equal(415, withBadCharset.Status);
    }

    public static void MissingHeaders_Answer400()
    {
        using var spine = Spine.Create();
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var response = spine.Handler.HandleAsync(new WebhookHttpRequest("POST", headers, Encoding.UTF8.GetBytes("{}"), "application/json")).GetAwaiter().GetResult();
        Assert.Equal(400, response.Status);
        Assert.Contains("x-hub-signature-256", response.Body ?? "", "the missing header is named");
    }

    public static void BodyOverTheCeiling_Answers413()
    {
        using var spine = Spine.Create(maxBodyBytes: 8);
        var response = spine.Handler.HandleAsync(ValidRequest("{\"too\":\"long\"}")).GetAwaiter().GetResult();
        Assert.Equal(413, response.Status);
    }

    public static void InvalidUtf8Body_Answers400()
    {
        using var spine = Spine.Create();
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-hub-signature-256"] = new[] { "sha256=0000000000000000000000000000000000000000000000000000000000000000" },
            ["x-github-delivery"] = new[] { "delivery-1" },
            ["x-github-event"] = new[] { "push" },
        };
        var response = spine.Handler.HandleAsync(new WebhookHttpRequest("POST", headers, new byte[] { 0xFF, 0xFE, 0x00 }, "application/json")).GetAwaiter().GetResult();
        Assert.Equal(400, response.Status);
    }

    public static void SecretUnavailable_Answers503()
    {
        using var spine = Spine.Create();
        var response = spine.Handler.HandleAsync(ValidRequest()).GetAwaiter().GetResult();
        Assert.Equal(503, response.Status);
        Assert.Contains("secret is unavailable", response.Body ?? "");
    }

    public static void WrongSignature_Answers401()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", Secret).GetAwaiter().GetResult();
        var response = spine.Handler.HandleAsync(ValidRequest(signature: "sha256=" + new string('0', 64))).GetAwaiter().GetResult();
        Assert.Equal(401, response.Status);
    }

    public static void MalformedSignature_Answers401()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", Secret).GetAwaiter().GetResult();
        var response = spine.Handler.HandleAsync(ValidRequest(signature: "sha1=abc")).GetAwaiter().GetResult();
        Assert.Equal(401, response.Status);
    }

    public static void ValidSignature_DispatchesAndAnswers202()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", Secret).GetAwaiter().GetResult();
        VerifiedWebhookDelivery? seen = null;
        using var rule = spine.Webhook.Register(new WebhookRule(new WebhookRuleId("r"), "github", (delivery, _) =>
        {
            seen = delivery;
            return Task.FromResult<WebhookSessionRequest?>(null);
        }));
        var response = spine.Handler.HandleAsync(ValidRequest()).GetAwaiter().GetResult();
        Assert.Equal(202, response.Status);
        Assert.WaitUntil(() => seen is not null, message: "the delivery reached the rule");
        Assert.Equal("github", seen!.Kind);
        Assert.Equal("delivery-1", seen.DeliveryId.Value);
        Assert.Equal("push", seen.Event.GetProperty("name").GetString());
        Assert.Equal("refs/heads/main", seen.Event.GetProperty("payload").GetProperty("ref").GetString());
    }

    public static void InvalidJson_Answers400()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", Secret).GetAwaiter().GetResult();
        var notJson = spine.Handler.HandleAsync(ValidRequest("not json")).GetAwaiter().GetResult();
        Assert.Equal(400, notJson.Status);
        var notObject = spine.Handler.HandleAsync(ValidRequest("[1,2]")).GetAwaiter().GetResult();
        Assert.Equal(400, notObject.Status);
        Assert.Contains("JSON object", notObject.Body ?? "");
    }

    public static void DispatchFailure_Answers503()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", Secret).GetAwaiter().GetResult();
        spine.Ctx.Dispose(); // closes the runtime; dispatch now fails loud
        var response = spine.Handler.HandleAsync(ValidRequest()).GetAwaiter().GetResult();
        Assert.Equal(503, response.Status);
    }
}
