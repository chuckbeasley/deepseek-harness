namespace Harness.Storage.Tests;

/// <summary>
/// One booted storage spine: a context with the JSON file provider over a fresh temp root.
/// </summary>
public sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }

    public required JsonFileStorageProvider Storage { get; init; }

    public required string Root { get; init; }

    /// <summary>Boot the spine with a fresh temp storage root.</summary>
    public static Harness Create()
    {
        var ctx = new Context();
        var root = Path.Combine(Path.GetTempPath(), "dsh-storage-tests-" + Guid.NewGuid().ToString("N"));
        var storage = new JsonFileStorageProvider(ctx, new JsonFileStorageConfig(root));
        return new Harness { Ctx = ctx, Storage = storage, Root = root };
    }

    /// <summary>Dispose the context (stopping the provider) and remove the temp root.</summary>
    public void Dispose()
    {
        Ctx.Dispose();
        if (Directory.Exists(Root))
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp root is test residue, not a test failure.
            }
        }
    }
}
