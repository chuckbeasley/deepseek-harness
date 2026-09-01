using System.Globalization;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Session;
using Harness.Storage;
using Harness.Workspace;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The workspace remotes over the durable registry: create/resolve idempotence, rename/delete/
/// reorder/membership/archive commands with their wire codes, and the live follow feed — all
/// through the registry so the wire codes ride the responses.
/// </summary>
public static class WorkspaceRemotesTests
{
    public static void Create_NewDirectory_AnswersTheView()
    {
        var (ctx, registry, _, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "project");
            Directory.CreateDirectory(dir);
            var response = Invoke(registry, "workspace/create", new { path = dir });
            Assert.True(response.Ok, "the create must succeed");
            var workspace = response.Result!.Value.GetProperty("workspace");
            Assert.True(response.Result.Value.GetProperty("created").GetBoolean(), "a fresh registration creates");
            Assert.True(workspace.GetProperty("workspaceId").GetString()!.Length > 0, "the workspace carries a stable id");
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), workspace.GetProperty("path").GetString());
            Assert.Equal("project", workspace.GetProperty("title").GetString());
            Assert.True(workspace.GetProperty("sessionIds").GetArrayLength() == 0, "no membership yet");
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
        var (ctx, registry, _, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "project");
            Directory.CreateDirectory(dir);
            var first = Invoke(registry, "workspace/create", new { path = dir });
            var second = Invoke(registry, "workspace/create", new { path = dir });
            Assert.True(first.Ok && second.Ok, "an idempotent re-registration succeeds");
            Assert.True(first.Result!.Value.GetProperty("created").GetBoolean(), "the first registration creates");
            Assert.False(second.Result!.Value.GetProperty("created").GetBoolean(), "the re-registration resolves the existing workspace");
            Assert.Equal(first.Result.Value.GetProperty("workspace").GetProperty("workspaceId").GetString(),
                second.Result.Value.GetProperty("workspace").GetProperty("workspaceId").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_MissingPath_SettlesInvalidPath()
    {
        var (ctx, registry, _, root) = Boot();
        try
        {
            var missing = Path.Combine(root, "missing");
            var response = Invoke(registry, "workspace/create", new { path = missing });
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

    public static void Create_SecondWorkspace_Succeeds()
    {
        var (ctx, registry, _, root) = Boot();
        try
        {
            var first = Path.Combine(root, "one");
            var second = Path.Combine(root, "two");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var opened = Invoke(registry, "workspace/create", new { path = first });
            Assert.True(opened.Ok, "the first registration succeeds");
            var another = Invoke(registry, "workspace/create", new { path = second });
            Assert.True(another.Ok, "the registry holds many workspaces");
            Assert.True(another.Result!.Value.GetProperty("created").GetBoolean());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_WithoutPathArg_SettlesBadRequest()
    {
        var (ctx, registry, _, root) = Boot();
        try
        {
            var response = Invoke(registry, "workspace/create", new { });
            Assert.False(response.Ok, "a missing path is refused");
            Assert.Equal("gateway/bad-request", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Rename_UpdatesTitle_AndSettlesTheWireCodes()
    {
        var (ctx, registry, seam, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            var created = seam.Create(dir);

            var renamed = Invoke(registry, "workspace/rename", new { workspaceId = created.Id.Value, title = "Alpha One" });
            Assert.True(renamed.Ok, "the rename succeeds");
            Assert.Equal("Alpha One", renamed.Result!.Value.GetProperty("workspace").GetProperty("title").GetString());

            var blank = Invoke(registry, "workspace/rename", new { workspaceId = created.Id.Value, title = "  " });
            Assert.False(blank.Ok, "a blank title is refused");
            Assert.Equal("gateway/bad-request", blank.Error!.Code);

            var secondDir = Path.Combine(root, "beta");
            Directory.CreateDirectory(secondDir);
            var second = seam.Create(secondDir);
            var conflict = Invoke(registry, "workspace/rename", new { workspaceId = second.Id.Value, title = "Alpha One" });
            Assert.False(conflict.Ok, "a duplicate title is refused");
            Assert.Equal("workspace/name-conflict", conflict.Error!.Code);
            Assert.Equal("Alpha One", conflict.Error.Details!.Value.GetProperty("name").GetString());

            var missing = Invoke(registry, "workspace/rename", new { workspaceId = "ghost", title = "Any" });
            Assert.False(missing.Ok, "a missing workspace is refused");
            Assert.Equal("workspace/not-found", missing.Error!.Code);
            Assert.Equal("ghost", missing.Error.Details!.Value.GetProperty("workspaceId").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Delete_Removes_SettlesNotFoundWhenAbsent()
    {
        var (ctx, registry, seam, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            var created = seam.Create(dir);

            var deleted = Invoke(registry, "workspace/delete", new { workspaceId = created.Id.Value });
            Assert.True(deleted.Ok, "the delete succeeds");
            Assert.True(deleted.Result!.Value.GetProperty("deleted").GetBoolean());

            var absent = Invoke(registry, "workspace/delete", new { workspaceId = created.Id.Value });
            Assert.False(absent.Ok, "an absent workspace is refused");
            Assert.Equal("workspace/not-found", absent.Error!.Code);
            Assert.Equal(created.Id.Value, absent.Error.Details!.Value.GetProperty("workspaceId").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void InsertBefore_MovesOrder_SettlesNotFound()
    {
        var (ctx, registry, seam, root) = Boot();
        try
        {
            var ids = new List<string>();
            foreach (var name in new[] { "a", "b", "c" })
            {
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                ids.Add(seam.Create(dir).Id.Value);
            }

            var moved = Invoke(registry, "workspace/insertBefore", new { workspaceId = ids[2], beforeWorkspaceId = ids[0] });
            Assert.True(moved.Ok, "the order move succeeds");
            var order = moved.Result!.Value.GetProperty("workspaceIds");
            Assert.Equal(ids[2], order[0].GetString());
            Assert.Equal(ids[0], order[1].GetString());

            var ghost = Invoke(registry, "workspace/insertBefore", new { workspaceId = "ghost" });
            Assert.False(ghost.Ok, "a missing workspace is refused");
            Assert.Equal("workspace/not-found", ghost.Error!.Code);
            Assert.Equal("ghost", ghost.Error.Details!.Value.GetProperty("workspaceId").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void InsertSessionBefore_MovesMembership_SettlesMoveInvalid()
    {
        var (ctx, registry, seam, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            var workspace = seam.Create(dir);
            _ = seam.AttachSession(workspace.Id, new SessionId("s-1"));
            _ = seam.AttachSession(workspace.Id, new SessionId("s-2"));
            _ = seam.AttachSession(workspace.Id, new SessionId("s-3"));

            var moved = Invoke(registry, "workspace/insertSessionBefore",
                new { workspaceId = workspace.Id.Value, sessionId = "s-3", beforeSessionId = "s-1" });
            Assert.True(moved.Ok, "the membership move succeeds");
            var sessions = moved.Result!.Value.GetProperty("workspace").GetProperty("sessionIds");
            Assert.Equal("s-3", sessions[0].GetString());
            Assert.Equal("s-1", sessions[1].GetString());

            var invalid = Invoke(registry, "workspace/insertSessionBefore",
                new { workspaceId = workspace.Id.Value, sessionId = "s-9" });
            Assert.False(invalid.Ok, "a non-member session is refused");
            Assert.Equal("workspace/move-invalid", invalid.Error!.Code);
            Assert.Equal(workspace.Id.Value, invalid.Error.Details!.Value.GetProperty("workspaceId").GetString());
            Assert.Equal("s-9", invalid.Error.Details.Value.GetProperty("sessionId").GetString());

            var ghost = Invoke(registry, "workspace/insertSessionBefore",
                new { workspaceId = "ghost", sessionId = "s-1" });
            Assert.False(ghost.Ok, "a missing workspace is refused");
            Assert.Equal("workspace/not-found", ghost.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void ArchiveSession_AddsToArchive_SettlesSessionNotFound()
    {
        var (ctx, registry, seam, root) = Boot();
        try
        {
            var alphaDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(alphaDir);
            _ = seam.Create(alphaDir);

            var archived = Invoke(registry, "workspace/archiveSession", new { sessionId = "s-known" });
            Assert.True(archived.Ok, "a known session archives");
            Assert.Equal("s-known", archived.Result!.Value.GetProperty("archivedSessionIds")[0].GetString());

            var unknown = Invoke(registry, "workspace/archiveSession", new { sessionId = "s-ghost" });
            Assert.False(unknown.Ok, "an unknown session is refused");
            Assert.Equal("session/not-found", unknown.Error!.Code);
            Assert.Equal("s-ghost", unknown.Error.Details!.Value.GetProperty("sessionId").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static async Task Follow_SendsBaselineThenDeltas()
    {
        var (ctx, registry, seam, root) = Boot();
        try
        {
            var firstDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(firstDir);
            var first = seam.Create(firstDir);

            var follow = global::Harness.Web.Host.WorkspaceRemotes.Follow(ctx);
            using var cts = new CancellationTokenSource();
            await using var enumerator = follow.Invoke(null, cts.Token).GetAsyncEnumerator();

            Assert.True(await enumerator.MoveNextAsync(), "the baseline frame arrives first");
            var baseline = enumerator.Current;
            Assert.Equal("baseline", baseline.GetProperty("type").GetString());
            var items = baseline.GetProperty("value").GetProperty("items");
            Assert.True(items.GetArrayLength() == 1, "the baseline lists the registered workspace");
            Assert.Equal(first.Id.Value, items[0].GetProperty("workspaceId").GetString());
            Assert.True(baseline.GetProperty("value").GetProperty("archivedSessionIds").GetArrayLength() == 0);

            var secondDir = Path.Combine(root, "beta");
            Directory.CreateDirectory(secondDir);
            var second = seam.Create(secondDir);
            Assert.True(await enumerator.MoveNextAsync(), "the upsert delta arrives");
            var upsert = enumerator.Current;
            Assert.Equal("upsert", upsert.GetProperty("type").GetString());
            Assert.Equal(second.Id.Value, upsert.GetProperty("workspace").GetProperty("workspaceId").GetString());
            // The create also appended to the display order: consume its order frame before moving.
            Assert.True(await enumerator.MoveNextAsync(), "the create order delta arrives");
            Assert.Equal("order", enumerator.Current.GetProperty("type").GetString());

            _ = seam.InsertBefore(second.Id, first.Id);
            Assert.True(await enumerator.MoveNextAsync(), "the order delta arrives");
            var order = enumerator.Current;
            Assert.Equal("order", order.GetProperty("type").GetString());
            Assert.Equal(second.Id.Value, order.GetProperty("workspaceIds")[0].GetString());

            Assert.True(seam.Delete(first.Id), "the delete commits");
            Assert.True(await enumerator.MoveNextAsync(), "the remove delta arrives");
            var removed = enumerator.Current;
            Assert.Equal("remove", removed.GetProperty("type").GetString());
            Assert.Equal(first.Id.Value, removed.GetProperty("workspaceId").GetString());
            // The delete also rewrote the display order: consume its order frame before the archive.
            Assert.True(await enumerator.MoveNextAsync(), "the delete order delta arrives");
            Assert.Equal("order", enumerator.Current.GetProperty("type").GetString());

            seam.ArchiveSession(new SessionId("s-known"));
            Assert.True(await enumerator.MoveNextAsync(), "the archived delta arrives");
            var archived = enumerator.Current;
            Assert.Equal("archived", archived.GetProperty("type").GetString());
            Assert.Equal("s-known", archived.GetProperty("archivedSessionIds")[0].GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    private static RpcResponse Invoke(DshRpcRegistry registry, string endpoint, object args)
        => registry.InvokeAsync(new RpcRequest(endpoint, JsonSerializer.SerializeToElement(args))).GetAwaiter().GetResult();

    private static (Context Ctx, DshRpcRegistry Registry, WorkspaceRegistry Seam, string Root) Boot()
    {
        var ctx = new Context();
        var root = Path.Combine(Path.GetTempPath(), "dsh-host-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new JsonFileStorageProvider(ctx, new JsonFileStorageConfig(Path.Combine(root, "store")));
        var known = new HashSet<string> { "s-known" };
        var seam = new WorkspaceRegistry(ctx, storage, id => known.Contains(id.Value));
        var registry = new DshRpcRegistry(ctx);
        _ = registry.Register(global::Harness.Web.Host.WorkspaceRemotes.Create(ctx));
        _ = registry.Register(global::Harness.Web.Host.WorkspaceRemotes.Rename(ctx));
        _ = registry.Register(global::Harness.Web.Host.WorkspaceRemotes.Delete(ctx));
        _ = registry.Register(global::Harness.Web.Host.WorkspaceRemotes.InsertBefore(ctx));
        _ = registry.Register(global::Harness.Web.Host.WorkspaceRemotes.InsertSessionBefore(ctx));
        _ = registry.Register(global::Harness.Web.Host.WorkspaceRemotes.ArchiveSession(ctx));
        return (ctx, registry, seam, root);
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
