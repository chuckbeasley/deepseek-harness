using System.Diagnostics;

namespace Harness.Lsp.Tests;

/// <summary>Shared spawn, temp-workspace, and polling helpers for the process-backed LSP suites.</summary>
internal static class LspTestHarness
{
    /// <summary>The fixture assembly, copied into the test output by the ProjectReference.</summary>
    public static string FixtureDll => Path.Combine(AppContext.BaseDirectory, "FakeLspServer.dll");

    /// <summary>The dotnet host used to run the fixture (DOTNET_HOST_PATH when present, else PATH).</summary>
    public static string DotNetHost()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv) ? fromEnv : "dotnet";
    }

    public static string FixtureCommand => DotNetHost();

    public static IReadOnlyList<string> FixtureArgs => new[] { "exec", FixtureDll };

    public static Dictionary<string, string> Env(params (string Key, string Value)[] entries)
        => entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    /// <summary>Create a temp root containing <c>ws/a.ts</c>; returns the root (the workspace is <c>root/ws</c>).</summary>
    public static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "hsh-lsp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ws"));
        File.WriteAllText(Path.Combine(root, "ws", "a.ts"), "const x = 1\n");
        return root;
    }

    public static string WorkspacePath(string root) => Path.Combine(root, "ws");

    public static string WorkspaceUri(string path) => new Uri(path).AbsoluteUri;

    /// <summary>Poll <paramref name="predicate"/> until it holds or the deadline elapses.</summary>
    public static async Task WaitForAsync(Func<bool> predicate, string message, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > deadline) throw new AssertionException($"waitFor timed out: {message}");
            await Task.Delay(20);
        }
    }

    /// <summary>Poll until a marker file exists, bounded so a broken handshake cannot hang the test.</summary>
    public static async Task WaitForFileAsync(string path, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!File.Exists(path))
        {
            if (Environment.TickCount64 > deadline) throw new AssertionException($"file never appeared: {path}");
            await Task.Delay(20);
        }
    }

    /// <summary>Probe a pid without changing its state (Windows: GetProcessById + HasExited).</summary>
    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Wait until a process can no longer execute so temp-workspace cleanup cannot race handle release.</summary>
    public static async Task WaitForProcessExit(int pid, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (ProcessAlive(pid))
        {
            if (Environment.TickCount64 > deadline) throw new AssertionException($"process {pid} did not exit");
            await Task.Delay(20);
        }
    }
}
