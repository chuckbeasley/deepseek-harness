using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Harness.Snapshot.Tests;

/// <summary>One completed CLI subprocess run.</summary>
public sealed record CliRunResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Subprocess driver for the snapshot suites: spawns the REAL hsh CLI with a temp home and cwd,
/// the snapshot overlay patch, and the snapshot-run environment, then harvests the persisted
/// session log from the profile sessions root.
/// </summary>
public static class SnapshotDriver
{
    /// <summary>The built hsh CLI assembly path (carried by the HshCliPath assembly metadata).</summary>
    public static string HshCliPath()
        => Metadata("HshCliPath");

    /// <summary>The repository root (carried by the RepoRoot assembly metadata).</summary>
    public static string RepoRoot()
        => Metadata("RepoRoot");

    /// <summary>The snapshot overlay patch that swaps the live LLM rows for the replay row.</summary>
    public static string SnapshotPatchPath()
        => Path.Combine(AppContext.BaseDirectory, "patches", "snapshot.cordis.yml");

    private static string Metadata(string key)
        => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key).Value
            ?? throw new InvalidOperationException($"assembly metadata {key} is missing");

    /// <summary>Create a fresh temp run directory pair (home + cwd); the caller owns cleanup.</summary>
    public static (string Home, string Cwd) CreateRunDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "hsh-snap-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var cwd = Path.Combine(root, "cwd");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cwd);
        return (home, cwd);
    }

    /// <summary>Seed the run cwd from a scenario workspace directory (when one exists).</summary>
    public static void SeedWorkspace(string cwd, string? workspaceDir)
    {
        if (workspaceDir is null || !Directory.Exists(workspaceDir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(workspaceDir))
        {
            var target = Path.Combine(cwd, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }

    /// <summary>Run one headless profile invocation with the snapshot environment.</summary>
    public static CliRunResult RunHeadless(
        string home,
        string cwd,
        string task,
        string fixtureFile,
        string? provider = null,
        string? model = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(HshCliPath());
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add("headless");
        startInfo.ArgumentList.Add("--patch");
        startInfo.ArgumentList.Add(SnapshotPatchPath());
        startInfo.ArgumentList.Add(task);
        startInfo.Environment["HSH_HOME"] = home;
        startInfo.Environment["HSH_SNAPSHOT_FILE"] = fixtureFile;
        startInfo.Environment["HSH_TELEMETRY_DISABLED"] = "1";
        // The recorded fixtures run POSIX shell commands; on Windows route bash through the
        // Git-bash sh when present so the recorded behavior reproduces.
        if (OperatingSystem.IsWindows())
        {
            var gitSh = new[] { @"C:\Program Files\Git\bin\sh.exe", @"C:\Program Files (x86)\Git\bin\sh.exe" }
                .FirstOrDefault(File.Exists);
            if (gitSh is not null) startInfo.Environment["HSH_SHELL_PATH"] = gitSh;
        }
        if (provider is not null) startInfo.Environment["HSH_SNAPSHOT_PROVIDER"] = provider;
        if (model is not null) startInfo.Environment["HSH_SNAPSHOT_MODEL"] = model;
        if (extraEnv is not null)
        {
            foreach (var pair in extraEnv) startInfo.Environment[pair.Key] = pair.Value;
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start the hsh CLI subprocess");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new AssertionException("the hsh CLI subprocess hung");
        }
        return new CliRunResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Harvest the primary persisted session log under a profile home, or <c>null</c>. With child
    /// sessions (subagent scenarios) the top-level session — the one whose header names no
    /// parent — is the recorded primary fixture.
    /// </summary>
    public static string? HarvestSessionLog(string home, string profile = "headless")
    {
        var root = Path.Combine(home, "profiles", profile, "sessions");
        if (!Directory.Exists(root)) return null;
        string? fallback = null;
        foreach (var file in Directory.EnumerateFiles(root, "session.jsonl", SearchOption.AllDirectories))
        {
            var first = File.ReadLines(file).FirstOrDefault();
            if (first is null) continue;
            if (!first.Contains("\"parentSession\"", StringComparison.Ordinal)) return File.ReadAllText(file);
            fallback ??= File.ReadAllText(file);
        }
        return fallback;
    }

    /// <summary>
    /// Harvest every persisted session log under a profile home (the primary plus any child
    /// sessions), ordered by the session header <c>createdAt</c> then id — the same ordering the
    /// recorded fixture set uses, so a child-session reference in the primary log redacts to the
    /// token the fixture pinned. An empty array means no log was persisted.
    /// </summary>
    public static string[] HarvestSessionLogs(string home, string profile = "headless")
    {
        var root = Path.Combine(home, "profiles", profile, "sessions");
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var logs = new List<(long CreatedAt, string Id, string Text)>();
        foreach (var file in Directory.EnumerateFiles(root, "session.jsonl", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var first = File.ReadLines(file).FirstOrDefault();
            if (first is null) continue;
            var createdAt = 0L;
            var id = string.Empty;
            using (var document = JsonDocument.Parse(first))
            {
                if (document.RootElement.TryGetProperty("createdAt", out var created)) createdAt = created.GetInt64();
                if (document.RootElement.TryGetProperty("id", out var sessionId)) id = sessionId.GetString() ?? string.Empty;
            }
            logs.Add((createdAt, id, text));
        }
        return logs
            .OrderBy(log => log.CreatedAt)
            .ThenBy(log => log.Id, StringComparer.Ordinal)
            .Select(log => log.Text)
            .ToArray();
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, destination);
            else File.Copy(entry, destination);
        }
    }
}