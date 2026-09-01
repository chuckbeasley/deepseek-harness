using System.Text.RegularExpressions;

namespace Harness.Cordis.Cosmokit;

/// <summary>Workspace path and display helpers (port of <c>@deepseek-ai/dsh-util-workspace-path</c>).</summary>
public static class WorkspacePaths
{
    /// <summary>
    /// Resolves a Workspace-relative path into the spelling used by path
    /// operations: absolute (POSIX or Windows-style) paths pass through, a
    /// relative path is joined onto <paramref name="cwd"/> when one is
    /// available, and otherwise the original path is returned.
    /// </summary>
    public static string ResolveWorkspacePath(string? cwd, string path)
    {
        if (path.StartsWith('/') || IsWindowsStylePath(path)) return path;
        if (string.IsNullOrEmpty(cwd)) return path;
        var basePath = Regex.Replace(cwd, "[/\\\\]+$", string.Empty);
        var relative = Regex.Replace(path, "^[/\\\\]+", string.Empty);
        return $"{basePath}/{relative}";
    }

    /// <summary>
    /// Abbreviates a POSIX home directory for display: the home itself becomes
    /// <c>~</c> and its descendants <c>~/...</c>. Windows-style paths or an
    /// absent <paramref name="home"/> return the path unchanged.
    /// </summary>
    public static string AbbreviateHomePath(string path, string? home = null)
    {
        if (string.IsNullOrEmpty(home)) return path;
        if (IsWindowsStylePath(path) || IsWindowsStylePath(home)) return path;
        var root = Regex.Replace(home, "/+$", string.Empty);
        if (root.Length == 0 || root == "/") return path;
        if (Regex.Replace(path, "/+$", string.Empty) == root) return "~";
        if (path.StartsWith(root + "/", StringComparison.Ordinal)) return "~" + path[root.Length..];
        return path;
    }

    /// <summary>
    /// Reads the final non-empty segment of a Workspace path for display.
    /// Returns an empty string for a separator-only path.
    /// </summary>
    public static string WorkspaceTitleOf(string path)
    {
        var trimmed = Regex.Replace(path, "[/\\\\]+$", string.Empty);
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return trimmed[(separator + 1)..];
    }

    private static bool IsWindowsStylePath(string value)
        => WindowsDrive.IsMatch(value) || value.StartsWith("\\\\", StringComparison.Ordinal);

    private static readonly Regex WindowsDrive = new("^[A-Za-z]:[/\\\\]");
}

