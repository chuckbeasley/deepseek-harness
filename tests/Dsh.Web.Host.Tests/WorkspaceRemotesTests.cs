using System.Globalization;
using Cordis.Core;
using Dsh.Web.Host;
using Dsh.Workspace;

namespace Dsh.Web.Host.Tests;

/// <summary>
/// The workspace remotes: the create command over the ported lifecycle, its idempotent reuse, and
/// the workspace/invalid-path classification, all through the registry so the wire codes ride the
/// responses.
/// </summary>
public static class WorkspaceRemotesTests
{
    public static void Create_NewDirectory_AnswersTheView()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot();
        try
        {
            var dir = Path.Combine(root, "project");
            Directory.CreateDirectory(dir);
            var args = JsonSerializer.SerializeToElement(new { path = dir });
            var response = registry.InvokeAsync(new RpcRequest("workspace/create", args)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the create must succeed");
            var workspace = response.Result!.Value.GetProperty("workspace");
            Assert.True(response.Result.Value.GetProperty("created").GetBoolean(), "a fresh open creates");
            Assert.True(workspace.GetProperty("workspaceId").GetString()!.Length > 0, "the workspace carries a stable id");
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), workspace.GetProperty("path").GetString());
            Assert.Equal("project", workspace.GetProperty("title").GetString());
            Assert.True(workspace.GetProperty("sessionIds").GetArrayLength() == 0, "session accounting is deferred");
            Assert.True(DateTimeOffset.TryParse(workspace.GetProperty("createdAt").GetString(),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _), "createdAt is ISO-8601");
            Assert.True(DateTimeOffset.TryParse(workspace.GetProperty("updatedAt").GetString(),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _), "updatedAt is ISO-8601");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_SamePathAgain_AnswersNotCreated()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot();
        try
        {
            var dir = Path.Combine(root, "project");
            Directory.CreateDirectory(dir);
            var args = JsonSerializer.SerializeToElement(new { path = dir });
            var first = registry.InvokeAsync(new RpcRequest("workspace/create", args)).GetAwaiter().GetResult();
            var second = registry.InvokeAsync(new RpcRequest("workspace/create", args)).GetAwaiter().GetResult();
            Assert.True(first.Ok && second.Ok, "an idempotent re-open succeeds");
            Assert.True(first.Result!.Value.GetProperty("created").GetBoolean(), "the first open creates");
            Assert.False(second.Result!.Value.GetProperty("created").GetBoolean(), "the re-open resolves the existing workspace");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_MissingPath_SettlesInvalidPath()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot();
        try
        {
            var missing = Path.Combine(root, "missing");
            var args = JsonSerializer.SerializeToElement(new { path = missing });
            var response = registry.InvokeAsync(new RpcRequest("workspace/create", args)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a nonexistent directory is refused");
            Assert.Equal("workspace/invalid-path", response.Error!.Code);
            Assert.Equal(missing, response.Error.Details!.Value.GetProperty("path").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_WhileAnotherOpen_SettlesInvalidPath()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot();
        try
        {
            var first = Path.Combine(root, "one");
            var second = Path.Combine(root, "two");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var opened = registry.InvokeAsync(new RpcRequest("workspace/create",
                JsonSerializer.SerializeToElement(new { path = first }))).GetAwaiter().GetResult();
            Assert.True(opened.Ok, "the first open succeeds");
            var response = registry.InvokeAsync(new RpcRequest("workspace/create",
                JsonSerializer.SerializeToElement(new { path = second }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "the single-slot lifecycle refuses a second open");
            Assert.Equal("workspace/invalid-path", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_WithoutPath_SettlesBadRequest()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot();
        try
        {
            var response = registry.InvokeAsync(new RpcRequest("workspace/create",
                JsonSerializer.SerializeToElement(new { }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a missing path is refused");
            Assert.Equal("gateway/bad-request", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    private static (Context Ctx, DshRpcRegistry Registry, LocalWorkspaceProvider Workspace) Boot()
    {
        var ctx = new Context();
        var workspace = new LocalWorkspaceProvider(ctx);
        var registry = new DshRpcRegistry(ctx);
        _ = registry.Register(Dsh.Web.Host.WorkspaceRemotes.Create(ctx));
        return (ctx, registry, workspace);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-host-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
