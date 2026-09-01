namespace Harness.Skill.Tests;

/// <summary>One temp directory removed on dispose; keeps test fixtures off the worktree.</summary>
public sealed class TempRoot : IDisposable
{
    private TempRoot(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    /// <summary>The temp directory path.</summary>
    public string Path { get; }

    /// <summary>Create a fresh temp directory.</summary>
    public static TempRoot Create()
        => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-skill-tests-" + Guid.NewGuid().ToString("N")));

    /// <summary>Remove the temp directory recursively.</summary>
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
