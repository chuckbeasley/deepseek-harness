using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Cordis.Core;
using Dsh.Web.App;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The bilingual shell copy dictionary: the fallback chain (active → English → key), the
/// Accept-Language negotiation, and the negotiated language reaching the prerendered shell.
/// </summary>
public static class WebLocaleTests
{
    public static void EnglishResolvesItsOwnKeys_UnknownKeysRenderAsThemselves()
    {
        Assert.Equal("Sessions", WebLocale.English.T("sessions"), "the English dictionary carries its own copy");
        Assert.Equal("No sessions yet. Send a message to start one.", WebLocale.English.T("noSessions"), "the English dictionary carries its own copy");
        Assert.Equal("new session", WebLocale.English.T("newSession"), "the English dictionary carries its own copy");
        Assert.Equal("Select a session to view its transcript.", WebLocale.English.T("selectSession"), "the English dictionary carries its own copy");
        Assert.Equal("Message dsh...", WebLocale.English.T("messagePlaceholder"), "the English dictionary carries its own copy");
        Assert.Equal("Send", WebLocale.English.T("send"), "the English dictionary carries its own copy");
        Assert.Equal("running", WebLocale.English.T("running"), "the English dictionary carries its own copy");
        Assert.Equal("queued", WebLocale.English.T("queued"), "the English dictionary carries its own copy");
        Assert.Equal("last error:", WebLocale.English.T("lastError"), "the English dictionary carries its own copy");
        Assert.Equal("missing.key", WebLocale.English.T("missing.key"), "an unknown key renders as itself");
        Assert.Equal(WebLocale.EnglishLanguage, WebLocale.English.Language, "the English instance names its locale");
    }

    public static void ChineseResolvesItsOwnKeys()
    {
        Assert.Equal("会话", WebLocale.Chinese.T("sessions"), "the zh dictionary carries its own copy");
        Assert.Equal("发送", WebLocale.Chinese.T("send"), "the zh dictionary carries its own copy");
        Assert.Equal("最近错误：", WebLocale.Chinese.T("lastError"), "the zh dictionary carries its own copy");
        Assert.Equal("missing.key", WebLocale.Chinese.T("missing.key"), "a key in neither dictionary renders as itself");
        Assert.Equal(WebLocale.ChineseLanguage, WebLocale.Chinese.Language, "the Chinese instance names its locale");
    }

    public static void NegotiatePicksTheFirstSupportedLanguage()
    {
        Assert.Equal("en", WebLocale.Negotiate(null), "an absent header is English");
        Assert.Equal("en", WebLocale.Negotiate(""), "an empty header is English");
        Assert.Equal("en", WebLocale.Negotiate("  "), "a blank header is English");
        Assert.Equal("en", WebLocale.Negotiate("fr-FR"), "an unsupported language is English");
        Assert.Equal("en", WebLocale.Negotiate("fr-FR,en-US;q=0.8"), "the first supported language wins in header order");
        Assert.Equal("en", WebLocale.Negotiate("en-US,en;q=0.9,zh-CN;q=0.8"), "English wins when listed first");
        Assert.Equal("zh", WebLocale.Negotiate("zh-CN,zh;q=0.9,en;q=0.8"), "Simplified Chinese wins when listed first");
        Assert.Equal("zh", WebLocale.Negotiate("fr-FR,zh-CN;q=0.9"), "a supported later language still wins");
        Assert.Equal("zh", WebLocale.Negotiate("ZH-TW"), "the primary subtag decides, case-insensitively");
    }

    public static void ForUnknownLanguageFallsBackToEnglish()
    {
        Assert.True(ReferenceEquals(WebLocale.For("fr"), WebLocale.English), "an unknown locale id resolves to English");
        Assert.True(ReferenceEquals(WebLocale.For("en"), WebLocale.English), "the en id resolves to English");
        Assert.True(ReferenceEquals(WebLocale.For("zh"), WebLocale.Chinese), "the zh id resolves to Chinese");
        Assert.True(ReferenceEquals(WebLocale.For("ZH"), WebLocale.Chinese), "the locale id is case-insensitive");
    }

    public static void ShellPrerendersInTheNegotiatedLocale()
    {
        var (ctx, host, client) = Boot(out _);
        try
        {
            var chinese = new HttpRequestMessage(HttpMethod.Get, "/");
            chinese.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            var zh = client.SendAsync(chinese).GetAwaiter().GetResult();
            var zhBody = zh.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True((int)zh.StatusCode == 200, "the shell serves with a Chinese language header");
            // Razor HTML-encodes the non-ASCII copy, so the assertions match the encoded forms.
            Assert.True(zhBody.Contains("&#x4F1A;&#x8BDD;", StringComparison.Ordinal), "the prerendered shell carries the zh copy");
            Assert.True(zhBody.Contains("&#x53D1;&#x9001;", StringComparison.Ordinal), "the prerendered shell carries the zh copy");

            var english = new HttpRequestMessage(HttpMethod.Get, "/");
            english.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            var en = client.SendAsync(english).GetAwaiter().GetResult();
            var enBody = en.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True(enBody.Contains("Sessions", StringComparison.Ordinal), "the English header keeps the English copy");
            Assert.True(enBody.Contains("Send", StringComparison.Ordinal), "the English header keeps the English copy");
        }
        finally
        {
            client.Dispose();
            host.StopAsync().GetAwaiter().GetResult();
            ctx.Dispose();
        }
    }

    private static (Context Ctx, WebHostService Host, HttpClient Client) Boot(out string origin)
    {
        var ctx = new Context();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new WebHostService(ctx, new WebHostConfig(Port: port, AuthFence: false),
            configure: builder => builder.Services.AddDshApp(),
            map: app => app.MapDshApp());
        host.StartAsync().GetAwaiter().GetResult();
        origin = host.ListenUrl!;
        return (ctx, host, new HttpClient { BaseAddress = new Uri(origin) });
    }
}
