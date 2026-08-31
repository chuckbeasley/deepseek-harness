namespace Dsh.Sdk.Client.Tests;

/// <summary>One disposable temp directory used as the child's DSH_HOME and cwd.</summary>
internal sealed class TempDir : IDisposable
{
    private TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-sdk-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public static TempDir Create() => new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // best-effort cleanup; the OS temp is the fallback
        }
    }
}

/// <summary>The built dsh CLI entry and per-test runtime resolution.</summary>
internal static class Runtime
{
    public static string CliPath { get; } = LoadCliPath();

    /// <summary>Resolve the launch spec for one real-runtime client: the built CLI, a fresh home, and a temp cwd.</summary>
    public static RuntimeProcessOptions Resolve(string dshHome, string cwd)
        => SdkLaunch.ResolveLaunch(new HarnessClientOptions { DshBin = CliPath, DshHome = dshHome, ProcessCwd = cwd },
            Environment.CurrentDirectory);

    /// <summary>A child that never answers: powershell sleeping with all three stdio pipes redirected.</summary>
    public static RuntimeProcessOptions SilentChild()
        => new(
            "powershell",
            new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" },
            null,
            () => Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.Ordinal),
            "silent child",
            10_000,
            null,
            ShutdownTimeoutMs: 1000,
            DisposeEofGraceMs: 500,
            DisposeGraceMs: 2000);

    private static string LoadCliPath()
    {
        var metadata = typeof(Runtime).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DshCliPath");
        var path = metadata?.Value;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"the test build did not locate the dsh CLI at \"{path}\"");
        }
        return path!;
    }
}
