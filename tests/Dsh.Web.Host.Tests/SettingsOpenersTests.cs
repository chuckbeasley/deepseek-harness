using System.Text.Json;
using Cordis.Core;
using Dsh.Preset;
using Dsh.Settings;
using Dsh.Web.Host;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The settings openers: document materialization + native open, the preset-directory open with
/// its not-found/read-only-shaped refusals, and the no-opener path — all through the registry so
/// the wire codes ride the responses. Native opens are faked; the production opener shell-opens
/// through the OS desktop handler.
/// </summary>
public static class SettingsOpenersTests
{
    public static void OpenSettingsDocument_MaterializesAndOpens()
    {
        var root = TempRoot();
        var documentPath = Path.Combine(root, "settings.json");
        var opened = new List<string>();
        var (ctx, registry) = Boot(documentPath);
        try
        {
            var fake = new SettingsOpeners(OpenPath: _ => Task.CompletedTask, OpenTextFile: path => { opened.Add(path); return Task.CompletedTask; }, CanOpen: true);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenSettingsDocument(ctx, fake));
            var response = registry.InvokeAsync(new RpcRequest("settings/openSettingsDocument", null)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the document open must succeed");
            Assert.True(response.Result!.Value.GetProperty("opened").GetBoolean());
            Assert.True(File.Exists(documentPath), "the absent document was materialized");
            Assert.Single(opened, "the fake opener received the document");
            Assert.Equal(documentPath, opened[0]);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void OpenSettingsDocument_NoLocalDocument_SettlesInternal()
    {
        var ctx = new Context();
        try
        {
            _ = new NoDocumentSettingsProvider(ctx);
            var registry = new DshRpcRegistry(ctx);
            var fake = new SettingsOpeners(OpenPath: _ => Task.CompletedTask, OpenTextFile: _ => Task.CompletedTask, CanOpen: true);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenSettingsDocument(ctx, fake));
            var response = registry.InvokeAsync(new RpcRequest("settings/openSettingsDocument", null)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a provider without a local document is refused");
            Assert.Equal("gateway/internal", response.Error!.Code);
            Assert.True(response.Error.Message.Contains("no local document", StringComparison.Ordinal));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void OpenSettingsDocument_OpenerFailure_SettlesInternal()
    {
        var root = TempRoot();
        var (ctx, registry) = Boot(Path.Combine(root, "settings.json"));
        try
        {
            var fake = new SettingsOpeners(
                OpenPath: _ => Task.CompletedTask,
                OpenTextFile: _ => throw new InvalidOperationException("no editor on this box"),
                CanOpen: true);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenSettingsDocument(ctx, fake));
            var response = registry.InvokeAsync(new RpcRequest("settings/openSettingsDocument", null)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "an opener failure is an infrastructure failure");
            Assert.Equal("gateway/internal", response.Error!.Code);
            Assert.True(response.Error.Message.Contains("path open failed", StringComparison.Ordinal));
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void CanOpenAgentPresetDirectory_AnswersTheDeploymentFact()
    {
        // One context per deployment shape: the rpc service registers once per context.
        var noOpener = new SettingsOpeners(OpenPath: _ => Task.CompletedTask, OpenTextFile: _ => Task.CompletedTask, CanOpen: false);
        var withoutCtx = new Context();
        try
        {
            var registry = new DshRpcRegistry(withoutCtx);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.CanOpenAgentPresetDirectory(withoutCtx, noOpener));
            var without = registry.InvokeAsync(new RpcRequest("settings/canOpenAgentPresetDirectory", null)).GetAwaiter().GetResult();
            Assert.False(without.Result!.Value.GetBoolean(), "a deployment without a native opener answers false");
        }
        finally
        {
            withoutCtx.Dispose();
        }

        var defaultCtx = new Context();
        try
        {
            var registry = new DshRpcRegistry(defaultCtx);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.CanOpenAgentPresetDirectory(defaultCtx));
            var withDefault = registry.InvokeAsync(new RpcRequest("settings/canOpenAgentPresetDirectory", null)).GetAwaiter().GetResult();
            Assert.True(withDefault.Result!.Value.GetBoolean(), "the production opener exists");
        }
        finally
        {
            defaultCtx.Dispose();
        }
    }

    public static void OpenAgentPresetDirectory_EmptyId_SettlesBadRequest()
    {
        var ctx = new Context();
        try
        {
            var registry = new DshRpcRegistry(ctx);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenAgentPresetDirectory(ctx));
            var response = registry.InvokeAsync(new RpcRequest("settings/openAgentPresetDirectory",
                JsonSerializer.SerializeToElement(new { agentPreset = "" }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "an empty preset id is refused");
            Assert.Equal("gateway/bad-request", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void OpenAgentPresetDirectory_NoPresetService_SettlesNotFound()
    {
        var ctx = new Context();
        try
        {
            var registry = new DshRpcRegistry(ctx);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenAgentPresetDirectory(ctx));
            var response = registry.InvokeAsync(new RpcRequest("settings/openAgentPresetDirectory",
                JsonSerializer.SerializeToElement(new { agentPreset = "any" }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "no preset service means no presets");
            Assert.Equal("agent-preset/not-found", response.Error!.Code);
            var details = response.Error.Details!.Value;
            Assert.Equal("any", details.GetProperty("agentPreset").GetString());
            Assert.True(details.GetProperty("available").GetArrayLength() == 0);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void OpenAgentPresetDirectory_MissingPreset_SettlesNotFound()
    {
        var root = TempRoot();
        var ctx = new Context();
        try
        {
            ctx.Set("preset", new FilePresetProvider(root));
            var registry = new DshRpcRegistry(ctx);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenAgentPresetDirectory(ctx));
            var response = registry.InvokeAsync(new RpcRequest("settings/openAgentPresetDirectory",
                JsonSerializer.SerializeToElement(new { agentPreset = "ghost" }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a missing preset id is refused");
            Assert.Equal("agent-preset/not-found", response.Error!.Code);
            Assert.True(response.Error.Message.Contains("not found", StringComparison.Ordinal));
            Assert.Equal("ghost", response.Error.Details!.Value.GetProperty("agentPreset").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void OpenAgentPresetDirectory_WithoutNativeOpener_ReturnsThePath()
    {
        var root = TempRoot();
        var ctx = new Context();
        try
        {
            var presetDir = Path.Combine(root, "writer");
            Directory.CreateDirectory(presetDir);
            File.WriteAllText(Path.Combine(presetDir, FilePresetProvider.CompositionFile), "- id: tools\n  name: tools\n");
            ctx.Set("preset", new FilePresetProvider(root));
            var registry = new DshRpcRegistry(ctx);
            var noOpener = new SettingsOpeners(OpenPath: _ => Task.CompletedTask, OpenTextFile: _ => Task.CompletedTask, CanOpen: false);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenAgentPresetDirectory(ctx, noOpener));
            var response = registry.InvokeAsync(new RpcRequest("settings/openAgentPresetDirectory",
                JsonSerializer.SerializeToElement(new { agentPreset = "writer" }))).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the path fallback is a success result");
            Assert.False(response.Result!.Value.GetProperty("opened").GetBoolean());
            Assert.Equal(presetDir, response.Result.Value.GetProperty("path").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void OpenAgentPresetDirectory_OpensThroughTheFake()
    {
        var root = TempRoot();
        var ctx = new Context();
        try
        {
            var presetDir = Path.Combine(root, "writer");
            Directory.CreateDirectory(presetDir);
            File.WriteAllText(Path.Combine(presetDir, FilePresetProvider.CompositionFile), "- id: tools\n  name: tools\n");
            ctx.Set("preset", new FilePresetProvider(root));
            var opened = new List<string>();
            var registry = new DshRpcRegistry(ctx);
            var fake = new SettingsOpeners(OpenPath: path => { opened.Add(path); return Task.CompletedTask; }, OpenTextFile: _ => Task.CompletedTask, CanOpen: true);
            _ = registry.Register(Dsh.Web.Host.SettingsRemotes.OpenAgentPresetDirectory(ctx, fake));
            var response = registry.InvokeAsync(new RpcRequest("settings/openAgentPresetDirectory",
                JsonSerializer.SerializeToElement(new { agentPreset = "writer" }))).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the native open succeeds");
            Assert.True(response.Result!.Value.GetProperty("opened").GetBoolean());
            Assert.Single(opened, "the fake opener received the preset directory");
            Assert.Equal(presetDir, opened[0]);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    private static (Context Ctx, DshRpcRegistry Registry) Boot(string documentPath)
    {
        var ctx = new Context();
        var settings = new FileSettingsProvider(ctx, documentPath);
        settings.StartAsync().GetAwaiter().GetResult();
        var registry = new DshRpcRegistry(ctx);
        return (ctx, registry);
    }

    /// <summary>A settings provider with no local document (the non-file storage case).</summary>
    private sealed class NoDocumentSettingsProvider : SettingsProvider
    {
        public NoDocumentSettingsProvider(Context ctx)
            : base(ctx)
        {
        }

        public override bool Writable => true;

        protected override Task<Dictionary<string, object?>> LoadAsync() => Task.FromResult(new Dictionary<string, object?>());

        protected override Task PersistAsync(SettingsNamespace ns, Dictionary<string, object?> section) => Task.CompletedTask;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-host-openers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
