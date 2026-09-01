using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The directoryPicker stub: every verb answers <c>directory-picker/unavailable</c> (no picking
/// backend is composed), and the create name grammar is still enforced before the capability
/// refusal.
/// </summary>
public static class DirectoryPickerTests
{
    public static void Pick_AnswersUnavailable()
    {
        var (ctx, registry) = Boot();
        try
        {
            var response = registry.InvokeAsync(new RpcRequest("directoryPicker/pick", null)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "no native backend is composed");
            Assert.Equal("directory-picker/unavailable", response.Error!.Code);
            Assert.True(response.Error.Details.HasValue, "the refusal carries the capability detail");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void List_AnswersUnavailable()
    {
        var (ctx, registry) = Boot();
        try
        {
            var response = registry.InvokeAsync(new RpcRequest("directoryPicker/list", null)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "no browse backend is composed");
            Assert.Equal("directory-picker/unavailable", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void CreateDirectory_BadName_SettlesBadRequest()
    {
        var (ctx, registry) = Boot();
        try
        {
            foreach (var name in new[] { "a/b", "..", ".", "", " " })
            {
                var response = registry.InvokeAsync(new RpcRequest("directoryPicker/createDirectory",
                    JsonSerializer.SerializeToElement(new { path = "C:\\tmp", name }))).GetAwaiter().GetResult();
                Assert.False(response.Ok, $"name \"{name}\" is refused");
                Assert.Equal("gateway/bad-request", response.Error!.Code);
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void CreateDirectory_ValidName_AnswersUnavailable()
    {
        var (ctx, registry) = Boot();
        try
        {
            var response = registry.InvokeAsync(new RpcRequest("directoryPicker/createDirectory",
                JsonSerializer.SerializeToElement(new { path = "C:\\tmp", name = "project" }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "the valid name still hits the missing capability");
            Assert.Equal("directory-picker/unavailable", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    private static (Context Ctx, DshRpcRegistry Registry) Boot()
    {
        var ctx = new Context();
        var registry = new DshRpcRegistry(ctx);
        _ = registry.Register(global::Harness.Web.Host.DirectoryPickerRemotes.Pick(ctx));
        _ = registry.Register(global::Harness.Web.Host.DirectoryPickerRemotes.List(ctx));
        _ = registry.Register(global::Harness.Web.Host.DirectoryPickerRemotes.CreateDirectory(ctx));
        return (ctx, registry);
    }
}
