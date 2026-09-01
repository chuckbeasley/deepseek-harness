namespace Harness.Spill.Tests;

/// <summary>
/// One booted spill spine: a context with the local spill provider over a fresh temp root, plus a
/// sibling directory OUTSIDE the root for containment-violation fixtures.
/// </summary>
public sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }

    public required LocalSpillProvider Spill { get; init; }

    /// <summary>The spill root every registered path lives inside.</summary>
    public required string Root { get; init; }

    /// <summary>A temp directory outside the spill root.</summary>
    public required string OutsideDir { get; init; }

    /// <summary>Boot the spine with a fresh temp spill root.</summary>
    public static Harness Create()
    {
        var ctx = new Context();
        var root = Path.Combine(Path.GetTempPath(), "hsh-spill-tests-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "hsh-spill-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var spill = new LocalSpillProvider(ctx, new SpillProviderConfig(root));
        return new Harness { Ctx = ctx, Spill = spill, Root = root, OutsideDir = outside };
    }

    /// <summary>Dispose the context (running provider teardown) and remove both temp directories.</summary>
    public void Dispose()
    {
        Ctx.Dispose();
        foreach (var dir in new[] { Root, OutsideDir })
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception)
                {
                    // A leftover temp dir is test residue, not a test failure.
                }
            }
        }
    }
}
