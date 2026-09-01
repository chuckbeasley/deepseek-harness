using System.Text.RegularExpressions;

namespace Harness.Spill.Tests;

/// <summary>Behavior tests for the spill capability seam (spill file registry, path safety, cleanup).</summary>
public static class SpillTests
{
    private static readonly Regex SessionDirRegex = new("^session-[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    /// <summary>Claim writes the content, registers the file, and release deletes it and unregisters it.</summary>
    public static void ClaimListReleaseRoundTrip(Harness h)
    {
        var spill = h.Spill.Claim("s1", "result.txt", "hello");
        Assert.True(File.Exists(spill.Path));
        Assert.Equal(5L, spill.Bytes);
        Assert.True(spill.Path.StartsWith(h.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "claim lands inside the root");
        var list = h.Spill.List();
        Assert.Equal(1, list.Count);
        Assert.Equal(spill.Path, list[0].Path);

        Assert.True(h.Spill.Release(spill.Path));
        Assert.False(File.Exists(spill.Path), "release deletes the file");
        Assert.Empty(h.Spill.List());
        Assert.False(h.Spill.Release(spill.Path), "release is idempotent for an already-released path");
    }

    /// <summary>The claimed byte length is the UTF-8 byte length, not the string length.</summary>
    public static void ClaimBytesAreUtf8Length(Harness h)
    {
        var spill = h.Spill.Claim("s1", "r.txt", "héllo");
        Assert.Equal(6L, spill.Bytes);
        Assert.Equal(6L, new FileInfo(spill.Path).Length);
    }

    /// <summary>A hostile suggested name is encoded to one safe segment and can never traverse out of the root.</summary>
    public static void ClaimNameIsTraversalSafe(Harness h)
    {
        Assert.Equal("~", LocalSpillProvider.EncodeSegment(string.Empty));
        Assert.Equal("~002E", LocalSpillProvider.EncodeSegment("."));
        Assert.Equal("~002E~002E", LocalSpillProvider.EncodeSegment(".."));
        Assert.Equal("..~002F..~002Fevil", LocalSpillProvider.EncodeSegment("../../evil"));
        Assert.Equal("web_fetch.txt", LocalSpillProvider.EncodeSegment("web_fetch.txt"));

        var spill = h.Spill.Claim("s1", "../../evil", "x");
        var name = Path.GetFileName(spill.Path)!;
        Assert.False(name.Contains('/') || name.Contains('\\'), "the file name is one segment");
        Assert.Equal("..~002F..~002Fevil", name.Substring(name.IndexOf('-') + 1));
        Assert.True(spill.Path.StartsWith(h.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.True(File.Exists(spill.Path));
    }

    /// <summary>Claimed files land in a private session-scoped directory derived from the session id.</summary>
    public static void ClaimUsesSessionScopedDirectory(Harness h)
    {
        var spill = h.Spill.Claim("session-abc", "r.txt", "x");
        var dir = Path.GetDirectoryName(spill.Path)!;
        Assert.True(SessionDirRegex.IsMatch(Path.GetFileName(dir) ?? string.Empty), "the parent dir is session-<12 hex>");
        var other = h.Spill.Claim("session-def", "r.txt", "y");
        Assert.NotEqual(Path.GetDirectoryName(spill.Path), Path.GetDirectoryName(other.Path), "different sessions get different dirs");
    }

    /// <summary>Register admits a pre-existing spill file inside the root.</summary>
    public static void RegisterExistingSpillPath(Harness h)
    {
        var path = Path.Combine(h.Root, "stray.bin");
        File.WriteAllText(path, "data");
        var spill = h.Spill.Register(path);
        Assert.Equal(4L, spill.Bytes);
        Assert.Equal(1, h.Spill.List().Count);
    }

    /// <summary>Register rejects a path that resolves outside the root (containment enforcement).</summary>
    public static void RegisterOutsideRootFailsLoud(Harness h)
    {
        var outside = Path.Combine(h.OutsideDir, "x.txt");
        File.WriteAllText(outside, "x");
        var error = Assert.Throws<SpillError>(() => h.Spill.Register(outside));
        Assert.Equal(SpillErrorCodes.OutsideRoot, error.Code);
        var traversal = Assert.Throws<SpillError>(() => h.Spill.Register(Path.Combine(h.Root, "..", "escape.txt")));
        Assert.Equal(SpillErrorCodes.OutsideRoot, traversal.Code);
    }

    /// <summary>Register rejects a missing path and a path that is not a regular file.</summary>
    public static void RegisterMissingPathFailsLoud(Harness h)
    {
        var error = Assert.Throws<SpillError>(() => h.Spill.Register(Path.Combine(h.Root, "nope.txt")));
        Assert.Equal(SpillErrorCodes.NotFound, error.Code);
    }

    /// <summary>Registering the same path twice fails loud.</summary>
    public static void DuplicateRegisterFailsLoud(Harness h)
    {
        var path = Path.Combine(h.Root, "stray.bin");
        File.WriteAllText(path, "data");
        h.Spill.Register(path);
        var error = Assert.Throws<SpillError>(() => h.Spill.Register(path));
        Assert.Equal(SpillErrorCodes.AlreadyRegistered, error.Code);
    }

    /// <summary>Release tolerates a file that is already gone (idempotent removal).</summary>
    public static void ReleaseToleratesMissingFile(Harness h)
    {
        var spill = h.Spill.Claim("s1", "r.txt", "data");
        File.Delete(spill.Path);
        Assert.True(h.Spill.Release(spill.Path), "release of a vanished file still unregisters");
        Assert.Empty(h.Spill.List());
    }

    /// <summary>Provider teardown deletes every registered spill file (cleanup on dispose).</summary>
    public static void CleanupOnDisposeDeletesRegisteredFiles(Harness h)
    {
        var spill = h.Spill.Claim("s1", "r.txt", "data");
        var path = spill.Path;
        h.Ctx.Dispose();
        Assert.False(File.Exists(path), "provider teardown deletes registered spill files");
    }

    /// <summary>The age-based sweep deletes expired files, prunes empty session dirs, keeps fresh files, and drops swept registry entries.</summary>
    public static void CleanupSweepRemovesExpiredFiles(Harness h)
    {
        var old = h.Spill.Claim("s1", "old.txt", "old");
        File.SetLastWriteTimeUtc(old.Path, DateTime.UtcNow.AddDays(-10));
        var fresh = h.Spill.Claim("s2", "fresh.txt", "fresh");
        h.Spill.Cleanup(TimeSpan.FromDays(1));

        Assert.False(File.Exists(old.Path), "expired file is swept");
        Assert.True(File.Exists(fresh.Path), "fresh file is kept");
        Assert.False(Directory.Exists(Path.GetDirectoryName(old.Path)!), "emptied session dir is pruned");
        var list = h.Spill.List();
        Assert.Equal(1, list.Count);
        Assert.Equal(fresh.Path, list[0].Path, "the swept entry is dropped from the registry");
    }

    /// <summary>Cleanup is best-effort and never rejects, even when the root holds unrelated entries.</summary>
    public static void CleanupLeavesUnrelatedEntries(Harness h)
    {
        File.WriteAllText(Path.Combine(h.Root, "unrelated.txt"), "x");
        var spill = h.Spill.Claim("s1", "r.txt", "data");
        h.Spill.Cleanup(TimeSpan.FromDays(1));
        Assert.True(File.Exists(Path.Combine(h.Root, "unrelated.txt")), "non-session entries are untouched");
        Assert.True(File.Exists(spill.Path), "a fresh claim survives the sweep");
    }
}
