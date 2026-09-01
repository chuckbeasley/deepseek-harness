namespace Harness.Fs.Tests;

/// <summary>Provider-level coverage of <see cref="LocalFileSystemProvider"/> over one workspace root.</summary>
public static class FileSystemServiceTests
{
    public static void TextWriteReadRoundTrip(Harness h)
    {
        var content = "line one\nline two\n";
        var outcome = h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("notes.txt", content))).GetAwaiter().GetResult();
        Assert.Equal("create", outcome.Operation);
        Assert.Equal("notes.txt", outcome.Version.Value is string ? "notes.txt" : "notes.txt");

        var read = h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("notes.txt"))).GetAwaiter().GetResult();
        Assert.Equal(content, read);

        var update = h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("notes.txt", "replaced"))).GetAwaiter().GetResult();
        Assert.Equal("update", update.Operation);
        Assert.Equal("replaced", h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("notes.txt"))).GetAwaiter().GetResult());
    }

    public static void BinaryBytesRoundTrip(Harness h)
    {
        var bytes = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF };
        File.WriteAllBytes(Path.Combine(h.WorkspaceRoot, "blob.bin"), bytes);
        var read = h.Fs.ReadBytesAsync(h.Fs.ResolveReadBytes(new FsReadBytesRequest("blob.bin", 1024))).GetAwaiter().GetResult();
        Assert.Equal(bytes, read, "binary round trip");
    }

    public static void ReadTextRejectsBinaryFile(Harness h)
    {
        File.WriteAllBytes(Path.Combine(h.WorkspaceRoot, "blob.bin"), new byte[] { 0x41, 0x00, 0x42 });
        var error = Assert.Throws<FsError>(() => h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("blob.bin"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotText, error.Code);
        Assert.True(error.Message.Contains("binary file"), "message names the binary rejection");
    }

    public static void ReadTextRejectsInvalidUtf8(Harness h)
    {
        File.WriteAllBytes(Path.Combine(h.WorkspaceRoot, "bad.txt"), new byte[] { 0xFF, 0xFE, 0x41 });
        var error = Assert.Throws<FsError>(() => h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("bad.txt"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotText, error.Code);
        Assert.True(error.Message.Contains("invalid UTF-8"), "message names the decode rejection");
    }

    public static void ReadBytesRejectsOversized(Harness h)
    {
        File.WriteAllText(Path.Combine(h.WorkspaceRoot, "big.txt"), new string('x', 100));
        var error = Assert.Throws<FsError>(() => h.Fs.ReadBytesAsync(h.Fs.ResolveReadBytes(new FsReadBytesRequest("big.txt", 50))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.TooLarge, error.Code);
    }

    public static void ListStatDeleteMkdir(Harness h)
    {
        h.Fs.MkdirAsync(h.Fs.ResolveMkdir(new FsMkdirRequest("sub"))).GetAwaiter().GetResult();
        Assert.True(Directory.Exists(Path.Combine(h.WorkspaceRoot, "sub")), "mkdir created the directory");

        var write = h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("sub/a.txt", "hello"))).GetAwaiter().GetResult();
        Assert.Equal("create", write.Operation);

        var sub = h.Fs.ListAsync(h.Fs.ResolveList(new FsListRequest("sub"))).GetAwaiter().GetResult();
        Assert.Equal(1, sub.Count);
        Assert.Equal("a.txt", sub[0].Name);
        Assert.Equal(FsPathType.File, sub[0].Type);
        Assert.True(sub[0].Target.DisplayPath.EndsWith("/sub/a.txt", StringComparison.Ordinal), "the display path is the absolute TS-style path");

        var root = h.Fs.ListAsync(h.Fs.ResolveList(new FsListRequest("."))).GetAwaiter().GetResult();
        Assert.Equal(1, root.Count);
        Assert.Equal("sub", root[0].Name);
        Assert.Equal(FsPathType.Directory, root[0].Type);

        var info = h.Fs.StatAsync(h.Fs.ResolveStat(new FsStatRequest("sub/a.txt"))).GetAwaiter().GetResult();
        Assert.NotNull(info);
        Assert.Equal(FsPathType.File, info!.Type);
        Assert.Equal(5L, info.Size);

        h.Fs.DeleteAsync(h.Fs.ResolveDelete(new FsDeleteRequest("sub/a.txt"))).GetAwaiter().GetResult();
        Assert.Null(h.Fs.StatAsync(h.Fs.ResolveStat(new FsStatRequest("sub/a.txt"))).GetAwaiter().GetResult());

        h.Fs.DeleteAsync(h.Fs.ResolveDelete(new FsDeleteRequest("sub"))).GetAwaiter().GetResult();
        Assert.Equal(0, h.Fs.ListAsync(h.Fs.ResolveList(new FsListRequest("."))).GetAwaiter().GetResult().Count);
    }

    public static void TraversalEscapesRootFailsLoud(Harness h)
    {
        var dotDot = Assert.Throws<FsError>(() => h.Fs.ResolveRead(new FsReadRequest("..\\outside.txt")));
        Assert.Equal(FsErrorCodes.SandboxDenied, dotDot.Code);

        var nested = Assert.Throws<FsError>(() => h.Fs.ResolveRead(new FsReadRequest("sub/../../outside.txt")));
        Assert.Equal(FsErrorCodes.SandboxDenied, nested.Code);

        var other = Path.Combine(Path.GetTempPath(), "dsh-fs-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var absolute = Assert.Throws<FsError>(() => h.Fs.ResolveRead(new FsReadRequest(Path.Combine(other, "x.txt"))));
            Assert.Equal(FsErrorCodes.SandboxDenied, absolute.Code);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    public static void MissingFilesMapToTypedError(Harness h)
    {
        var read = Assert.Throws<FsError>(() => h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("missing.txt"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotFound, read.Code);

        var bytes = Assert.Throws<FsError>(() => h.Fs.ReadBytesAsync(h.Fs.ResolveReadBytes(new FsReadBytesRequest("missing.txt", 100))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotFound, bytes.Code);

        var list = Assert.Throws<FsError>(() => h.Fs.ListAsync(h.Fs.ResolveList(new FsListRequest("missing"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotFound, list.Code);

        Assert.Null(h.Fs.StatAsync(h.Fs.ResolveStat(new FsStatRequest("missing.txt"))).GetAwaiter().GetResult());

        var del = Assert.Throws<FsError>(() => h.Fs.DeleteAsync(h.Fs.ResolveDelete(new FsDeleteRequest("missing.txt"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotFound, del.Code);
    }

    public static void WorkspaceRootResolutionHonorsExplicitSpec(Harness h)
    {
        var spec = h.Fs.ResolveWrite(new FsWriteRequest("a/b/c.txt", "deep"));
        Assert.True(spec.Target.DisplayPath.EndsWith("/a/b/c.txt", StringComparison.Ordinal), "the display path is the absolute TS-style path");
        Assert.Equal(Path.Combine(h.WorkspaceRoot, "a", "b", "c.txt"), spec.Target.TargetKey.Value);
        h.Fs.WriteTextAsync(spec).GetAwaiter().GetResult();
        Assert.True(File.Exists(Path.Combine(h.WorkspaceRoot, "a", "b", "c.txt")), "write landed at the spec's key");
        Assert.Equal("deep", h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("a/b/c.txt"))).GetAwaiter().GetResult());

        // ".." segments that stay inside the root normalize and resolve.
        var dotDot = h.Fs.ResolveWrite(new FsWriteRequest("a/b/../d.txt", "x"));
        Assert.True(dotDot.Target.DisplayPath.EndsWith("/a/d.txt", StringComparison.Ordinal), "the display path is the absolute TS-style path");
        h.Fs.WriteTextAsync(dotDot).GetAwaiter().GetResult();
        Assert.True(File.Exists(Path.Combine(h.WorkspaceRoot, "a", "d.txt")), "contained .. normalized into the root");
    }

    public static void WriteIntentsGuardMutations(Harness h)
    {
        var first = h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("g.txt", "one"))).GetAwaiter().GetResult();
        Assert.Equal("create", first.Operation);

        var notObserved = Assert.Throws<FsError>(() => h.Fs.WriteTextAsync(
            h.Fs.ResolveWrite(new FsWriteRequest("g.txt", "two", new FsCreateIfAbsentIntent()))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotObserved, notObserved.Code);

        var current = h.Fs.StatAsync(h.Fs.ResolveStat(new FsStatRequest("g.txt"))).GetAwaiter().GetResult();
        Assert.NotNull(current);

        var stale = Assert.Throws<FsError>(() => h.Fs.WriteTextAsync(
            h.Fs.ResolveWrite(new FsWriteRequest("g.txt", "two", new FsReplaceIfVersionIntent(new FsVersion("bogus"))))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.StaleVersion, stale.Code);

        var guarded = h.Fs.WriteTextAsync(h.Fs.ResolveWrite(
            new FsWriteRequest("g.txt", "two", new FsReplaceIfVersionIntent(current!.Version)))).GetAwaiter().GetResult();
        Assert.Equal("update", guarded.Operation);

        var missing = Assert.Throws<FsError>(() => h.Fs.WriteTextAsync(h.Fs.ResolveWrite(
            new FsWriteRequest("gone.txt", "x", new FsReplaceIfVersionIntent(current.Version)))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.StaleVersion, missing.Code);
    }

    public static void WriteRejectsNonRegularFileTarget(Harness h)
    {
        h.Fs.MkdirAsync(h.Fs.ResolveMkdir(new FsMkdirRequest("dir"))).GetAwaiter().GetResult();
        var error = Assert.Throws<FsError>(() => h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("dir", "x"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotRegularFile, error.Code);
    }

    public static void ReadRejectsDirectory(Harness h)
    {
        h.Fs.MkdirAsync(h.Fs.ResolveMkdir(new FsMkdirRequest("dir"))).GetAwaiter().GetResult();
        var error = Assert.Throws<FsError>(() => h.Fs.ReadTextAsync(h.Fs.ResolveRead(new FsReadRequest("dir"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotRegularFile, error.Code);
        var bytes = Assert.Throws<FsError>(() => h.Fs.ReadBytesAsync(h.Fs.ResolveReadBytes(new FsReadBytesRequest("dir", 100))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotRegularFile, bytes.Code);
    }

    public static void ListRejectsNonDirectory(Harness h)
    {
        h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("f.txt", "x"))).GetAwaiter().GetResult();
        var error = Assert.Throws<FsError>(() => h.Fs.ListAsync(h.Fs.ResolveList(new FsListRequest("f.txt"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.NotDirectory, error.Code);
    }

    public static void DeleteNonEmptyDirectoryFails(Harness h)
    {
        h.Fs.MkdirAsync(h.Fs.ResolveMkdir(new FsMkdirRequest("dir"))).GetAwaiter().GetResult();
        h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("dir/f.txt", "x"))).GetAwaiter().GetResult();
        var error = Assert.Throws<FsError>(() => h.Fs.DeleteAsync(h.Fs.ResolveDelete(new FsDeleteRequest("dir"))).GetAwaiter().GetResult());
        Assert.Equal(FsErrorCodes.IoError, error.Code);
    }

    public static void EmptyPathRejected(Harness h)
    {
        var error = Assert.Throws<FsError>(() => h.Fs.ResolveRead(new FsReadRequest("   ")));
        Assert.Equal(FsErrorCodes.NotFound, error.Code);
        Assert.True(error.Message.Contains("non-empty"), "message explains the requirement");
    }

    public static void VersionChangesOnOverwrite(Harness h)
    {
        h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("v.txt", "aaa"))).GetAwaiter().GetResult();
        var before = h.Fs.StatAsync(h.Fs.ResolveStat(new FsStatRequest("v.txt"))).GetAwaiter().GetResult();
        h.Fs.WriteTextAsync(h.Fs.ResolveWrite(new FsWriteRequest("v.txt", "bbbbbb"))).GetAwaiter().GetResult();
        var after = h.Fs.StatAsync(h.Fs.ResolveStat(new FsStatRequest("v.txt"))).GetAwaiter().GetResult();
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(before!.Version != after!.Version, "overwrite changes the version token");
    }
}
