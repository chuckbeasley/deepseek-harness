namespace Harness.Attachment.Tests;

/// <summary>
/// One booted attachment spine: a context with the local attachment provider over a fresh temp
/// root, plus a source directory holding ingest fixtures.
/// </summary>
public sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }

    public required LocalAttachmentProvider Attachments { get; init; }

    /// <summary>The attachment root holding one object file per id.</summary>
    public required string AttachmentRoot { get; init; }

    /// <summary>The temp base directory that owns every fixture.</summary>
    public required string BaseDir { get; init; }

    /// <summary>A directory holding ingest source files (outside the attachment root).</summary>
    public required string SourceDir { get; init; }

    /// <summary>Boot the spine with a fresh temp attachment root and source directory.</summary>
    public static Harness Create(long maxBytes = 1_000_000)
    {
        var ctx = new Context();
        var baseDir = Path.Combine(Path.GetTempPath(), "hsh-attachment-tests-" + Guid.NewGuid().ToString("N"));
        var attachmentRoot = Path.Combine(baseDir, "attachments");
        var sourceDir = Path.Combine(baseDir, "src");
        Directory.CreateDirectory(sourceDir);
        var attachments = new LocalAttachmentProvider(ctx, new AttachmentProviderConfig(attachmentRoot, maxBytes));
        return new Harness { Ctx = ctx, Attachments = attachments, AttachmentRoot = attachmentRoot, BaseDir = baseDir, SourceDir = sourceDir };
    }

    /// <summary>Write <paramref name="content"/> into the source directory and return its path.</summary>
    public string WriteSource(string fileName, string content)
    {
        var path = Path.Combine(SourceDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Dispose the context and remove the temp base directory.</summary>
    public void Dispose()
    {
        Ctx.Dispose();
        if (Directory.Exists(BaseDir))
        {
            try
            {
                Directory.Delete(BaseDir, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp base is test residue, not a test failure.
            }
        }
    }
}
