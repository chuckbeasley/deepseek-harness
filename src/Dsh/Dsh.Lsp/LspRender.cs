using System.Text.RegularExpressions;

namespace Dsh.Lsp;

/// <summary>
/// Pure formatting and coordinate conversion for the <c>lsp</c> tool (port of <c>tool-lsp/render.ts</c>):
/// workspace-grouped location rendering with <c>file:</c>-URI resolution, complete-result capping, and UI
/// presentation. No I/O — a UI may call the presenter on live streaming and on replay, so it depends only
/// on the tool arguments.
/// </summary>
public static class LspRender
{
    /// <summary>Default cap on rendered locations before an omission marker is appended.</summary>
    public const int DefaultMaxLocations = 100;

    /// <summary>Default cap on the complete rendered tool result, including truncation metadata.</summary>
    public const int DefaultMaxResultChars = 16_000;

    private static readonly Regex DrivePath = new(@"^\/[a-z](?::|%3A)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Render a locations result grouped by file, converting each zero-based location back to a one-based
    /// <c>path:line:character</c> entry. Applies <paramref name="maxLocations"/> and appends an omission
    /// marker when it truncates by count, then applies the complete result cap.
    /// </summary>
    /// <param name="locations">the seam's locations (possibly empty).</param>
    /// <param name="workspaceUri">the provider's canonical workspace <c>file:</c> URI.</param>
    /// <param name="maxLocations">the cap before truncation.</param>
    /// <param name="maxResultChars">the complete rendered-text cap, including truncation metadata.</param>
    /// <returns>the rendered text; a distinct no-result line when there are none.</returns>
    public static string FormatLocations(IReadOnlyList<LspLocation> locations, string workspaceUri, int maxLocations, int maxResultChars)
    {
        if (locations.Count == 0) return BoundResult("No results.", maxResultChars, "locations");
        var shown = locations.Take(maxLocations).ToList();
        var omitted = locations.Count - shown.Count;
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var location in shown)
        {
            var path = RenderUri(location.Uri, workspaceUri);
            var line = location.Range.Start.Line + 1;
            var character = location.Range.Start.Character + 1;
            if (!grouped.TryGetValue(path, out var entries))
            {
                entries = new List<string>();
                grouped[path] = entries;
            }
            entries.Add($"{path}:{line}:{character}");
        }
        var lines = new List<string>();
        foreach (var entries in grouped.Values) lines.AddRange(entries);
        if (omitted > 0)
        {
            lines.Add($"… {omitted} more location{(omitted == 1 ? string.Empty : "s")} omitted (limit {maxLocations}).");
        }
        return BoundResult(string.Join("\n", lines), maxResultChars, "locations");
    }

    /// <summary>Render a hover result, applying <paramref name="maxResultChars"/> last and keeping its marker within the cap.</summary>
    /// <param name="hover">the normalized hover, or null for no hover.</param>
    /// <param name="maxResultChars">the complete rendered-text cap, including truncation metadata.</param>
    /// <returns>the rendered hover text; a distinct no-result line for null.</returns>
    public static string FormatHover(LspHover? hover, int maxResultChars)
    {
        var text = hover is null ? "No hover information." : hover.Contents;
        return BoundResult(text, maxResultChars, "hover");
    }

    /// <summary>Bound a complete rendered result, including the truncation notice itself.</summary>
    /// <param name="text">the rendered text.</param>
    /// <param name="maxChars">the complete-result cap.</param>
    /// <param name="label">the truncation marker label.</param>
    /// <returns>the bounded text; the marker is inside the complete cap.</returns>
    public static string BoundResult(string text, int maxChars, string label)
    {
        if (text.Length <= maxChars) return text;
        var notice = $"\n… {label} truncated (limit {maxChars} characters).";
        if (notice.Length >= maxChars) return notice[..maxChars];
        return text[..(maxChars - notice.Length)] + notice;
    }

    /// <summary>
    /// Resolve a location URI without applying the harness host's path rules. A valid <c>file:</c> URI
    /// becomes workspace-relative when it is under the provider's canonical workspace URI, or a URI-derived
    /// absolute path otherwise; malformed and non-<c>file:</c> URIs remain verbatim.
    /// </summary>
    /// <param name="uri">the target URI from the seam.</param>
    /// <param name="workspaceUri">the provider's canonical workspace <c>file:</c> URI.</param>
    /// <returns>the display path or the verbatim URI.</returns>
    public static string RenderUri(string uri, string workspaceUri)
    {
        if (!uri.StartsWith("file:", StringComparison.Ordinal)) return uri;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target)) return uri;
        if (!Uri.TryCreate(workspaceUri, UriKind.Absolute, out var workspace)) return uri;
        if (!string.Equals(workspace.Scheme, "file", StringComparison.OrdinalIgnoreCase)) return uri;
        // A file: URI does not carry its world's OS, so a leading /X: segment is read as a Windows
        // drive. A POSIX workspace literally rooted at /c:/... would mis-render (display only; edits and
        // reads use the exact URI). The pathname keeps the WHATWG leading slash; Uri.AbsolutePath strips
        // it for drive paths on Windows hosts, which would mis-detect the world.
        var workspacePathName = UriPathName(workspace);
        var windowsWorld = workspace.Host.Length > 0 || DrivePath.IsMatch(workspacePathName);
        var targetPathName = UriPathName(target);
        var targetWindowsWorld = windowsWorld && (target.Host.Length > 0 || DrivePath.IsMatch(targetPathName));
        var workspacePath = FilePath(workspace.Host, workspacePathName, windowsWorld);
        var targetPath = FilePath(target.Host, targetPathName, targetWindowsWorld);
        if (workspacePath is null || targetPath is null) return uri;
        if (windowsWorld != targetWindowsWorld) return targetPath;
        var relative = RelativePath(workspacePath, targetPath, windowsWorld);
        var separator = windowsWorld ? '\\' : '/';
        var outside = relative == ".."
            || relative.StartsWith($"..{separator}", StringComparison.Ordinal)
            || IsAbsolute(relative, windowsWorld);
        var rendered = relative == "." ? "." : outside ? targetPath : relative;
        return windowsWorld ? rendered.Replace('\\', '/') : rendered;
    }

    /// <summary>UI presentation for a pending <c>lsp</c> call: a generic search card carrying the operation and one-based cursor.</summary>
    /// <param name="args">the raw tool arguments.</param>
    /// <returns>the generic call view.</returns>
    public static LspCallView PresentLspCall(LspToolArgs args)
        => new("generic", "search", $"LSP {args.Operation} {args.FilePath}:{args.Line}:{args.Character}", new[] { new LspCallLocation(args.FilePath, args.Line) });

    /// <summary>Decode a file URL path for its execution world while containing malformed URL failures.</summary>
    private static string? FilePath(string host, string pathName, bool windows)
    {
        if (host.Length > 0 && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return null;
        // fileURLToPath rejects encoded path separators (and NUL); keep those verbatim.
        if (pathName.Contains("%2F", StringComparison.OrdinalIgnoreCase) || pathName.Contains("%5C", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Uri.UnescapeDataString(pathName);
        if (path.Contains('\0')) return null;
        if (windows)
        {
            if (path.Length < 3 || path[0] != '/' || !char.IsLetter(path[1]) || path[2] != ':') return null;
            return path[1..].Replace('/', '\\');
        }
        return path.StartsWith("/", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>The WHATWG-style pathname of a file URI (always leading-slash), independent of the host OS.</summary>
    private static string UriPathName(Uri uri)
    {
        var original = uri.OriginalString;
        var schemeEnd = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return "/";
        var afterScheme = original[(schemeEnd + 3)..];
        var slash = afterScheme.IndexOf('/');
        return slash < 0 ? "/" : afterScheme[slash..];
    }

    /// <summary>Platform-independent relative computation matching Node's posix/win32 <c>relative</c> for the renderer's cases.</summary>
    private static string RelativePath(string from, string to, bool windows)
    {
        var separator = windows ? '\\' : '/';
        var fromParts = from.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var toParts = to.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var common = 0;
        while (common < fromParts.Length
               && common < toParts.Length
               && string.Equals(fromParts[common], toParts[common], comparison))
        {
            common++;
        }
        if (windows && common == 0) return to; // different drives: absolute
        var parts = new List<string>();
        for (var i = common; i < fromParts.Length; i++) parts.Add("..");
        for (var i = common; i < toParts.Length; i++) parts.Add(toParts[i]);
        return parts.Count == 0 ? "." : string.Join(separator, parts);
    }

    private static bool IsAbsolute(string path, bool windows)
        => windows
            ? path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'
            : path.StartsWith("/", StringComparison.Ordinal);
}
