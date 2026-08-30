using System.Text;

namespace Dsh.Attachment.Tests;

/// <summary>Behavior tests for the attachment capability seam (ingest/list/read/remove, size validation).</summary>
public static class AttachmentTests
{
    private static readonly byte[] HelloWorld = Encoding.UTF8.GetBytes("hello world");

    /// <summary>Ingest copies the source content into the attachment root under a generated id.</summary>
    public static void IngestCopiesContent(Harness h)
    {
        var source = h.WriteSource("a.txt", "hello world");
        var reference = h.Attachments.Ingest(source);
        Assert.Equal(11L, reference.Bytes);
        Assert.Equal(32, reference.Id.Value.Length, "the id is a generated uuid");
        Assert.True(File.Exists(Path.Combine(h.AttachmentRoot, reference.Id.Value)), "an object file exists under the id");
        Assert.Equal(HelloWorld, File.ReadAllBytes(Path.Combine(h.AttachmentRoot, reference.Id.Value)));
    }

    /// <summary>List returns the ingested attachments in ingestion order.</summary>
    public static void ListShowsIngested(Harness h)
    {
        var first = h.Attachments.Ingest(h.WriteSource("a.txt", "one"));
        var second = h.Attachments.Ingest(h.WriteSource("b.txt", "two"));
        var list = h.Attachments.List();
        Assert.Equal(2, list.Count);
        Assert.Equal(first.Id, list[0].Id);
        Assert.Equal(second.Id, list[1].Id);
        Assert.Equal("a.txt", list[0].Name);
        Assert.Equal("b.txt", list[1].Name);
    }

    /// <summary>Read returns the stored bytes and the matching reference.</summary>
    public static void ReadReturnsContent(Harness h)
    {
        var reference = h.Attachments.Ingest(h.WriteSource("a.txt", "hello world"));
        var data = h.Attachments.Read(reference.Id);
        Assert.Equal(reference.Id, data.Ref.Id);
        Assert.Equal(reference.Bytes, data.Ref.Bytes);
        Assert.Equal(HelloWorld, data.Content);
    }

    /// <summary>Remove deletes the object file and drops the registration; a second remove is a no-op.</summary>
    public static void RemoveDeletesFileAndUnregisters(Harness h)
    {
        var reference = h.Attachments.Ingest(h.WriteSource("a.txt", "hello world"));
        var path = Path.Combine(h.AttachmentRoot, reference.Id.Value);
        Assert.True(h.Attachments.Remove(reference.Id));
        Assert.False(File.Exists(path), "remove deletes the object file");
        Assert.Empty(h.Attachments.List());
        var readError = Assert.Throws<AttachmentError>(() => h.Attachments.Read(reference.Id));
        Assert.Equal(AttachmentErrorCodes.NotFound, readError.Code);
        Assert.False(h.Attachments.Remove(reference.Id), "a second remove is a no-op");
    }

    /// <summary>Content above the configured byte limit fails loud with ATTACHMENT_TOO_LARGE.</summary>
    public static void OversizedSourceFailsLoud(Harness h)
    {
        var harness = h;
        var ctx2 = new Context();
        using (var small = new LocalAttachmentProvider(ctx2, new AttachmentProviderConfig(harness.AttachmentRoot, 4)))
        {
            var source = harness.WriteSource("big.txt", "hello world");
            var error = Assert.Throws<AttachmentError>(() => small.Ingest(source));
            Assert.Equal(AttachmentErrorCodes.TooLarge, error.Code);
        }
    }

    /// <summary>An absent source fails loud with ATTACHMENT_NOT_FOUND.</summary>
    public static void AbsentSourceFailsLoud(Harness h)
    {
        var error = Assert.Throws<AttachmentError>(() => h.Attachments.Ingest(Path.Combine(h.SourceDir, "missing.txt")));
        Assert.Equal(AttachmentErrorCodes.NotFound, error.Code);
    }

    /// <summary>A directory source fails loud with ATTACHMENT_NOT_REGULAR_FILE.</summary>
    public static void DirectorySourceFailsLoud(Harness h)
    {
        var error = Assert.Throws<AttachmentError>(() => h.Attachments.Ingest(h.SourceDir));
        Assert.Equal(AttachmentErrorCodes.NotRegularFile, error.Code);
    }

    /// <summary>The display name is stripped of path information and control characters.</summary>
    public static void NameSanitizationStripsPathInfo(Harness h)
    {
        var source = h.WriteSource("real.txt", "x");
        var reference = h.Attachments.Ingest(source, "C:\\Users\\someone\\dir\\file name.txt");
        Assert.Equal("file name.txt", reference.Name);
        var control = h.Attachments.Ingest(source, "bad\u0001name.txt");
        Assert.Equal("badname.txt", control.Name);
        var empty = h.Attachments.Ingest(source, "   ");
        Assert.Equal(empty.Id.Value, empty.Name, "an unusable name falls back to the id");
    }

    /// <summary>Reading an unknown id fails loud with ATTACHMENT_NOT_FOUND.</summary>
    public static void ReadMissingIdFailsLoud(Harness h)
    {
        var error = Assert.Throws<AttachmentError>(() => h.Attachments.Read(new AttachmentId("deadbeefdeadbeefdeadbeefdeadbeef")));
        Assert.Equal(AttachmentErrorCodes.NotFound, error.Code);
    }

    /// <summary>A persisted object survives provider teardown and reads back on a fresh provider (name falls back to the id).</summary>
    public static void IngestPersistsAcrossProviders(Harness h)
    {
        var reference = h.Attachments.Ingest(h.WriteSource("a.txt", "hello world"));
        h.Ctx.Dispose();
        var ctx2 = new Context();
        using (var provider2 = new LocalAttachmentProvider(ctx2, new AttachmentProviderConfig(h.AttachmentRoot, 1_000_000)))
        {
            var data = provider2.Read(reference.Id);
            Assert.Equal(reference.Id, data.Ref.Id);
            Assert.Equal(reference.Id.Value, data.Ref.Name, "an unregistered object uses the id as its name");
            Assert.Equal(HelloWorld, data.Content);
        }
    }
}
