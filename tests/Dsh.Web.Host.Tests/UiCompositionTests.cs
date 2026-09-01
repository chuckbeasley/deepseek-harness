using System.Net;
using System.Net.Sockets;
using Harness.Cordis.Core;
using Harness.Storage;
using Harness.Ui.Approval;
using Harness.Ui.Chat;
using Harness.Ui.Plan;
using Harness.Ui.Sessions;
using Harness.Ui.Settings;
using Harness.Ui.Sidebar;
using Harness.Ui.Workspace;
using Harness.Workspace;
using Harness.Web.App;
using Harness.Web.App.Slots;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The ui-* slot contributions (the RCL port of the ui-* plugin set): each package registers its
/// components into the shell's slots, and the composed shell renders them in the prerendered
/// HTML — sidebar chrome, session list, composer, and the workspace list over the registry seam.
/// </summary>
public static class UiCompositionTests
{
    public static void TheUiContributions_RenderInThePrerenderedShell()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-ui-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ctx = new Context();
        var host = default(WebHostService);
        try
        {
            var workspaceDir = Path.Combine(root, "lab");
            Directory.CreateDirectory(workspaceDir);
            var storage = new JsonFileStorageProvider(ctx, new JsonFileStorageConfig(Path.Combine(root, "store")));
            var workspaces = new WorkspaceRegistry(ctx, storage);
            _ = workspaces.Create(workspaceDir, "lab");

            var slots = new SlotRegistry();
            var pageAssemblies = new PageAssemblyRegistry();
            var disposers = new IDisposable[]
            {
                UiSidebarPlugin.Apply(slots),
                UiSessionsPlugin.Apply(slots),
                UiChatPlugin.Apply(slots),
                UiWorkspacePlugin.Apply(slots),
                UiApprovalPlugin.Apply(slots),
                UiSettingsPlugin.Apply(slots, pageAssemblies),
                UiPlanPlugin.Apply(slots, pageAssemblies),
            };

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            host = new WebHostService(ctx, new WebHostConfig(Port: port, AuthFence: false),
                configure: builder => builder.Services.AddDshApp(slots, pageAssemblies),
                map: app => app.MapDshApp(pageAssemblies.List()));
            host.StartAsync().GetAwaiter().GetResult();

            using var client = new HttpClient { BaseAddress = new Uri(host.ListenUrl!) };
            var english = client.GetAsync("/").GetAwaiter().GetResult();
            var body = english.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True((int)english.StatusCode == 200, "the composed shell serves");
            Assert.True(body.Contains("dsh-brand-row", StringComparison.Ordinal), "the sidebar brand row renders");
            Assert.True(body.Contains("New Session", StringComparison.Ordinal), "the new-session action renders");
            Assert.True(body.Contains("Workspaces", StringComparison.Ordinal), "the workspace list renders its heading");
            Assert.True(body.Contains(">lab<", StringComparison.Ordinal), "the workspace list renders the registry's workspace");
            Assert.True(body.Contains("dsh-session-list", StringComparison.Ordinal), "the session-list contribution renders");
            Assert.True(body.Contains("class=\"dsh-input-row\"", StringComparison.Ordinal), "the composer contribution renders");
            Assert.True(body.Contains("No sessions yet. Send a message to start one.", StringComparison.Ordinal), "the session list empty state renders");

            var chinese = new HttpRequestMessage(HttpMethod.Get, "/");
            chinese.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            var zh = client.SendAsync(chinese).GetAwaiter().GetResult();
            var zhBody = zh.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            // 新建会话 (the new-session action) and 工作区 (workspaces), entity-encoded by Razor.
            Assert.True(zhBody.Contains("&#x65B0;&#x5EFA;&#x4F1A;&#x8BDD;", StringComparison.Ordinal), "the zh copy reaches the sidebar action");
            Assert.True(zhBody.Contains("&#x5DE5;&#x4F5C;&#x533A;", StringComparison.Ordinal), "the zh copy reaches the workspace heading");

            // The routed pages resolve through the assembly registry: /settings renders the
            // settings document surface, /plan renders the plan surface.
            var settings = client.GetAsync("/settings").GetAwaiter().GetResult();
            var settingsBody = settings.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True((int)settings.StatusCode == 200,
                $"the settings page serves (got {(int)settings.StatusCode}: {settingsBody[..Math.Min(300, settingsBody.Length)]})");
            Assert.True(settingsBody.Contains("dsh-page", StringComparison.Ordinal), "the settings page renders its shell");
            Assert.True(settingsBody.Contains("This deployment composes no settings provider.", StringComparison.Ordinal), "the settings page reports no provider");

            var plan = client.GetAsync("/plan").GetAwaiter().GetResult();
            var planBody = plan.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True((int)plan.StatusCode == 200, "the plan page serves");
            Assert.True(planBody.Contains("dsh-page", StringComparison.Ordinal), "the plan page renders its shell");
            Assert.True(planBody.Contains("Select a session to view its transcript.", StringComparison.Ordinal), "the plan page follows the shared selection");

            foreach (var disposer in disposers) disposer.Dispose();
        }
        finally
        {
            if (host is not null)
            {
                host.StopAsync().GetAwaiter().GetResult();
            }
            ctx.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
