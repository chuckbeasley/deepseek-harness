namespace Dsh.Attachment.Tests;

/// <summary>Zero-dependency console test runner for the attachment capability seam.</summary>
public static class Program
{
    private static readonly (string Name, Action<Harness> Run)[] Suites = new (string, Action<Harness>)[]
    {
        ("ingest copies content", AttachmentTests.IngestCopiesContent),
        ("list shows ingested attachments", AttachmentTests.ListShowsIngested),
        ("read returns the stored content", AttachmentTests.ReadReturnsContent),
        ("remove deletes the file and unregisters", AttachmentTests.RemoveDeletesFileAndUnregisters),
        ("oversized source fails loud", AttachmentTests.OversizedSourceFailsLoud),
        ("absent source fails loud", AttachmentTests.AbsentSourceFailsLoud),
        ("directory source fails loud", AttachmentTests.DirectorySourceFailsLoud),
        ("name sanitization strips path info", AttachmentTests.NameSanitizationStripsPathInfo),
        ("read of an unknown id fails loud", AttachmentTests.ReadMissingIdFailsLoud),
        ("objects persist across provider instances", AttachmentTests.IngestPersistsAcrossProviders),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                using var harness = Harness.Create();
                run(harness);
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL  {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}
