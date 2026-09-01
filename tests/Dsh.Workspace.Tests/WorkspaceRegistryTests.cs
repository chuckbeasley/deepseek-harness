using Harness.Session;
using Harness.Storage;

namespace Harness.Workspace.Tests;

/// <summary>
/// The durable workspace registry: create/resolve/rename/delete/order, explicit session
/// membership, the archive set, the change events, and persistence across instances — all over
/// the JSON storage backend.
/// </summary>
public static class WorkspaceRegistryTests
{
    public static void Create_RegistersAndResolvesByPath()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            var created = registry.Create(dir);
            Assert.False(string.IsNullOrEmpty(created.Id.Value), "the registry stamps a stable id");
            Assert.Equal("alpha", created.Title, "the title defaults to the directory name");
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), created.Root);
            Assert.Equal(created.Id, registry.Get(created.Id)?.Id);
            Assert.Equal(created.Id, registry.ResolveByPath(dir)?.Id, "resolveByPath finds the canonical path");
            Assert.Single(registry.List(), "the registry lists the workspace");
            Assert.Equal(created.Id, registry.Order()[0]);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_RejectsInvalidPaths()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var missing = Assert.Throws<WorkspaceError>(() => registry.Create(Path.Combine(root, "ghost")));
            Assert.Equal(WorkspaceErrorCodes.NotFound, missing.Code);

            var file = Path.Combine(root, "file.txt");
            File.WriteAllText(file, "x");
            var notDir = Assert.Throws<WorkspaceError>(() => registry.Create(file));
            Assert.Equal(WorkspaceErrorCodes.NotDirectory, notDir.Code);

            var empty = Assert.Throws<WorkspaceError>(() => registry.Create("   "));
            Assert.Equal(WorkspaceErrorCodes.InvalidPath, empty.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Create_DuplicatePath_Rejects()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            _ = registry.Create(dir);
            var duplicate = Assert.Throws<WorkspaceError>(() => registry.Create(dir));
            Assert.Equal(WorkspaceErrorCodes.DuplicatePath, duplicate.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Rename_UpdatesTitle_AndRejectsBlankAndConflicts()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var firstDir = Path.Combine(root, "alpha");
            var secondDir = Path.Combine(root, "beta");
            Directory.CreateDirectory(firstDir);
            Directory.CreateDirectory(secondDir);
            var first = registry.Create(firstDir);
            var second = registry.Create(secondDir);

            var renamed = registry.Rename(first.Id, "Alpha One");
            Assert.Equal("Alpha One", renamed.Title);
            Assert.Equal("Alpha One", registry.Get(first.Id)?.Title);

            var blank = Assert.Throws<WorkspaceError>(() => registry.Rename(first.Id, "  "));
            Assert.Equal(WorkspaceErrorCodes.InvalidTitle, blank.Code);

            var conflict = Assert.Throws<WorkspaceError>(() => registry.Rename(second.Id, "Alpha One"));
            Assert.Equal(WorkspaceErrorCodes.NameConflict, conflict.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Delete_RemovesAndUpdatesOrder()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var firstDir = Path.Combine(root, "alpha");
            var secondDir = Path.Combine(root, "beta");
            Directory.CreateDirectory(firstDir);
            Directory.CreateDirectory(secondDir);
            var first = registry.Create(firstDir);
            _ = registry.Create(secondDir);

            Assert.True(registry.Delete(first.Id), "the delete reports success");
            Assert.Null(registry.Get(first.Id), "the deleted workspace is gone");
            Assert.Single(registry.List(), "the other workspace remains");
            Assert.False(registry.Delete(first.Id), "deleting an absent workspace reports false");
            Assert.Equal("beta", registry.List()[0].Title);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void InsertBefore_MovesWithinTheDisplayOrder()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var ids = new List<WorkspaceId>();
            foreach (var name in new[] { "a", "b", "c" })
            {
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                ids.Add(registry.Create(dir).Id);
            }

            var moved = registry.InsertBefore(ids[2], ids[0]);
            Assert.Equal(new[] { ids[2], ids[0], ids[1] }, moved, "c moves before a");

            var appended = registry.InsertBefore(ids[0], null);
            Assert.Equal(new[] { ids[2], ids[1], ids[0] }, appended, "an anchor-less move appends");

            var self = Assert.Throws<WorkspaceError>(() => registry.InsertBefore(ids[0], ids[0]));
            Assert.Equal(WorkspaceErrorCodes.OrderInvalid, self.Code);

            var ghost = Assert.Throws<WorkspaceError>(() => registry.InsertBefore(ids[0], new WorkspaceId("ghost")));
            Assert.Equal(WorkspaceErrorCodes.NotFound, ghost.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void SessionMembership_AttachesAndMoves()
    {
        var (ctx, registry, root) = Boot();
        try
        {
            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            var workspace = registry.Create(dir);
            var s1 = new SessionId("s-1");
            var s2 = new SessionId("s-2");
            var s3 = new SessionId("s-3");

            _ = registry.AttachSession(workspace.Id, s1);
            _ = registry.AttachSession(workspace.Id, s2);
            var attached = registry.AttachSession(workspace.Id, s3);
            Assert.Equal(new[] { s1, s2, s3 }, attached.SessionIdsOrEmpty, "attachment appends in order");

            var moved = registry.InsertSessionBefore(workspace.Id, s3, s1);
            Assert.Equal(new[] { s3, s1, s2 }, moved.SessionIdsOrEmpty, "the session moves before its anchor");

            var nonMember = Assert.Throws<WorkspaceError>(() => registry.InsertSessionBefore(workspace.Id, new SessionId("s-9"), null));
            Assert.Equal(WorkspaceErrorCodes.MoveInvalid, nonMember.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void ArchiveSession_RequiresKnownSession()
    {
        var known = new HashSet<string> { "s-live" };
        var (ctx, registry, root) = Boot(id => known.Contains(id.Value));
        try
        {
            var ghost = Assert.Throws<WorkspaceError>(() => registry.ArchiveSession(new SessionId("s-ghost")));
            Assert.Equal(WorkspaceErrorCodes.UnknownSession, ghost.Code);

            registry.ArchiveSession(new SessionId("s-live"));
            Assert.Single(registry.ArchivedSessionIds, "the known session archives");
            registry.ArchiveSession(new SessionId("s-live"));
            Assert.Single(registry.ArchivedSessionIds, "re-archiving is a no-op");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Persistence_SurvivesRestart()
    {
        var (firstCtx, first, root) = Boot();
        WorkspaceId alphaId;
        try
        {
            var alphaDir = Path.Combine(root, "alpha");
            var betaDir = Path.Combine(root, "beta");
            Directory.CreateDirectory(alphaDir);
            Directory.CreateDirectory(betaDir);
            var alpha = first.Create(alphaDir);
            alphaId = alpha.Id;
            _ = first.Create(betaDir);
            _ = first.AttachSession(alpha.Id, new SessionId("s-1"));
            _ = first.Rename(alpha.Id, "Alpha One");
            first.InsertBefore(alpha.Id, null);
            first.ArchiveSession(new SessionId("s-1"));
        }
        finally
        {
            firstCtx.Dispose();
        }

        var (secondCtx, second, _) = Boot(root);
        try
        {
            Assert.Equal(2, second.List().Count, "the second instance reloads every workspace");
            Assert.Equal("Alpha One", second.Get(alphaId)?.Title, "the rename persisted");
            var alpha = second.Get(alphaId);
            Assert.NotNull(alpha, "the first workspace survives");
            Assert.Single(alpha!.SessionIdsOrEmpty, "the membership persisted");
            Assert.Equal(new SessionId("s-1"), alpha.SessionIdsOrEmpty[0]);
            Assert.Single(second.ArchivedSessionIds, "the archive set persisted");
            Assert.Equal(alphaId, second.Order()[1], "the display order persisted");
        }
        finally
        {
            secondCtx.Dispose();
            Cleanup(root);
        }
    }

    public static void Events_AreEmittedAfterCommittedMutations()
    {
        var (ctx, registry, root) = Boot(id => true);
        try
        {
            var upserts = new List<Workspace>();
            var orders = new List<string[]>();
            var archived = new List<string[]>();
            var removed = new List<WorkspaceId>();
            using var u = ctx.On<Workspace>("workspace/upserted", upserts.Add);
            using var o = ctx.On<string[]>("workspace/order", orders.Add);
            using var a = ctx.On<string[]>("workspace/archived", archived.Add);
            using var r = ctx.On<WorkspaceId>("workspace/removed", removed.Add);

            var dir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(dir);
            var workspace = registry.Create(dir);
            registry.ArchiveSession(new SessionId("s-1"));
            registry.Delete(workspace.Id);

            Assert.True(upserts.Count >= 1, "creation emits workspace/upserted");
            Assert.True(orders.Count >= 1, "creation emits workspace/order");
            Assert.Single(archived, "archive emits workspace/archived");
            Assert.Single(removed, "deletion emits workspace/removed");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    private static (Context Ctx, WorkspaceRegistry Registry, string Root) Boot(Func<SessionId, bool>? sessionKnown = null)
        => Boot(Path.Combine(Path.GetTempPath(), "dsh-workspace-registry-" + Guid.NewGuid().ToString("N")), sessionKnown);

    private static (Context Ctx, WorkspaceRegistry Registry, string Root) Boot(string root, Func<SessionId, bool>? sessionKnown = null)
    {
        var ctx = new Context();
        Directory.CreateDirectory(root);
        var storage = new JsonFileStorageProvider(ctx, new JsonFileStorageConfig(Path.Combine(root, "store")));
        var registry = new WorkspaceRegistry(ctx, storage, sessionKnown);
        return (ctx, registry, root);
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
