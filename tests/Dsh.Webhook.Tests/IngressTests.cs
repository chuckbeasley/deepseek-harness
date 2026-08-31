using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Cordis.Core;
using Dsh.Credentials;
using Dsh.Webhook;

namespace Dsh.Webhook.Tests;

/// <summary>
/// End-to-end behavior of <see cref="HttpListenerWebhookIngress"/> over real loopback HTTP: a
/// signed POST reaches a rule and answers 202; oversized bodies answer 413; stopping the ingress
/// closes the listener.
/// </summary>
public static class IngressTests
{
    /// <summary>Reserve one free loopback port, then release it (a benign race for tests).</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Spine : IDisposable
    {
        public required Context Ctx { get; init; }

        public required LocalCredentialsProvider Credentials { get; init; }

        public required WebhookRuntime Webhook { get; init; }

        public required HttpListenerWebhookIngress Ingress { get; init; }

        public required string Prefix { get; init; }

        public static Spine Create(int maxBodyBytes = 1024)
        {
            var ctx = new Context();
            var credentials = new LocalCredentialsProvider(ctx, new LocalCredentialsConfig(DshHome: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-webhook-ingress-" + Guid.NewGuid().ToString("N"))));
            var webhook = new WebhookRuntime(ctx);
            var port = FreePort();
            var prefix = $"http://127.0.0.1:{port}/webhook/";
            var handler = new GitHubWebhookHandler(
                ctx,
                webhook,
                credentials,
                new GitHubWebhookHandlerConfig(new WebhookSourceId("primary-github"), "GITHUB_WEBHOOK_SECRET", maxBodyBytes));
            var ingress = new HttpListenerWebhookIngress(ctx, new HttpListenerWebhookIngressConfig(prefix, handler.HandleAsync, maxBodyBytes));
            return new Spine { Ctx = ctx, Credentials = credentials, Webhook = webhook, Ingress = ingress, Prefix = prefix };
        }

        public void Dispose()
        {
            Ingress.StopAsync().GetAwaiter().GetResult();
            Ctx.Dispose();
        }
    }

    private static string Signature(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private static HttpRequestMessage SignedPost(string prefix, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, prefix);
        request.Headers.TryAddWithoutValidation("x-hub-signature-256", Signature("top-secret", body));
        request.Headers.TryAddWithoutValidation("x-github-delivery", "delivery-1");
        request.Headers.TryAddWithoutValidation("x-github-event", "push");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    public static void SignedPost_ReachesTheRule_AndAnswers202()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", "top-secret").GetAwaiter().GetResult();
        var seen = new List<string>();
        using var rule = spine.Webhook.Register(new WebhookRule(new WebhookRuleId("r"), "github", (delivery, _) =>
        {
            seen.Add(delivery.DeliveryId.Value);
            return Task.FromResult<WebhookSessionRequest?>(null);
        }));
        spine.Ingress.StartAsync().GetAwaiter().GetResult();
        using var client = new HttpClient();
        var response = client.SendAsync(SignedPost(spine.Prefix, "{\"ref\":\"refs/heads/main\"}")).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.WaitUntil(() => seen.Count == 1, message: "the rule received the delivery");
        Assert.Equal("delivery-1", seen[0]);
    }

    public static void BadSignature_Answers401()
    {
        using var spine = Spine.Create();
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", "top-secret").GetAwaiter().GetResult();
        spine.Ingress.StartAsync().GetAwaiter().GetResult();
        using var client = new HttpClient();
        var request = SignedPost(spine.Prefix, "{\"ref\":\"refs/heads/main\"}");
        request.Headers.Remove("x-hub-signature-256");
        request.Headers.TryAddWithoutValidation("x-hub-signature-256", "sha256=" + new string('0', 64));
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static void OversizedBody_Answers413()
    {
        using var spine = Spine.Create(maxBodyBytes: 8);
        spine.Credentials.SetAsync("GITHUB_WEBHOOK_SECRET", "top-secret").GetAwaiter().GetResult();
        spine.Ingress.StartAsync().GetAwaiter().GetResult();
        using var client = new HttpClient();
        var response = client.SendAsync(SignedPost(spine.Prefix, "{\"too\":\"long\"}")).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    public static void StoppingTheIngress_ClosesTheListener()
    {
        using var spine = Spine.Create();
        spine.Ingress.StartAsync().GetAwaiter().GetResult();
        spine.Ingress.StopAsync().GetAwaiter().GetResult();
        using var client = new HttpClient();
        var failed = false;
        try
        {
            client.SendAsync(SignedPost(spine.Prefix, "{}")).GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            failed = true;
        }
        Assert.True(failed, "a request after stop is refused at the socket");
    }
}
