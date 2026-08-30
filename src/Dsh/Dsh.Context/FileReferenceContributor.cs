using System.Text.RegularExpressions;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Context;

/// <summary>
/// Browser-safe <c>@file</c> mention grammar (port of grammar.ts): quoted <c>@"path"</c> and plain
/// <c>@path</c> tokens. Full-text scanning requires the <c>@</c> to start a token — preceded by
/// whitespace or a line start — so an <c>@</c> inside another token, such as an email address, is
/// not a reference.
/// </summary>
public static class FileReferenceGrammar
{
    private static readonly Regex MentionPattern = new(
        @"(?:^|\s)@(""([^""]*)""|([^\s""@]+))",
        RegexOptions.CultureInvariant);

    /// <summary>Extract the raw reference paths from one text value, in appearance order.</summary>
    public static IReadOnlyList<string> ExtractReferences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var references = new List<string>();
        foreach (Match match in MentionPattern.Matches(text))
        {
            var group = match.Groups[2].Success ? match.Groups[2] : match.Groups[3];
            references.Add(group.Value);
        }
        return references;
    }
}

/// <summary>
/// Local file-reference contributor (port of file-reference-local discovery plus resolution):
/// every <c>@path</c> mention in the session's user messages is resolved within the workspace root
/// and its content (or directory listing) contributed. Containment is enforced locally — Dsh.Fs
/// pins every path inside a workspace root too, but referencing it would drag the fs tool chain
/// into the context assembly, so the port repeats the containment rule with its own resolver
/// (documented port choice). References outside the root, absolute paths, traversal segments, and
/// missing targets fail loud; the TS discovery is advisory, so fail-loud resolution is a port
/// decision.
/// </summary>
public sealed class FileReferenceContributor : IContextContributor
{
    /// <summary>The contributor's stable key.</summary>
    public const string DefaultKey = "file-reference";

    private readonly string _workspaceRoot;
    private readonly int _maxBytesPerFile;
    private readonly int _maxEntriesPerDirectory;

    /// <summary>Create the contributor over one absolute workspace root.</summary>
    /// <param name="workspaceRoot">the root every reference must resolve inside.</param>
    /// <param name="maxBytesPerFile">UTF-16 character cap per contributed file; longer content is truncated with a notice.</param>
    /// <param name="maxEntriesPerDirectory">maximum listing entries contributed for a directory reference.</param>
    public FileReferenceContributor(string workspaceRoot, int maxBytesPerFile = 16 * 1024, int maxEntriesPerDirectory = 100)
    {
        if (string.IsNullOrEmpty(workspaceRoot))
        {
            throw new ArgumentException("workspaceRoot must be a non-empty path", nameof(workspaceRoot));
        }
        if (maxBytesPerFile <= 0)
        {
            throw new ArgumentException("maxBytesPerFile must be positive", nameof(maxBytesPerFile));
        }
        if (maxEntriesPerDirectory <= 0)
        {
            throw new ArgumentException("maxEntriesPerDirectory must be positive", nameof(maxEntriesPerDirectory));
        }
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _maxBytesPerFile = maxBytesPerFile;
        _maxEntriesPerDirectory = maxEntriesPerDirectory;
    }

    /// <summary>The absolute workspace root all references must resolve inside.</summary>
    public string WorkspaceRoot => _workspaceRoot;

    /// <inheritdoc />
    public string Key => DefaultKey;

    /// <inheritdoc />
    public Task<ContextSection?> ContributeAsync(Dsh.Session.Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var references = new List<string>();
        foreach (var evt in session.Events)
        {
            if (evt is not UserMessageEvent user) continue;
            foreach (var block in user.Message.Content)
            {
                if (block is TextBlock text) references.AddRange(FileReferenceGrammar.ExtractReferences(text.Text));
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (var reference in references)
        {
            if (reference.Length == 0) continue; // an empty @"" mention references nothing
            if (!seen.Add(reference)) continue;
            entries.Add(RenderResolvedReference(reference, cancellationToken));
        }
        if (entries.Count == 0) return Task.FromResult<ContextSection?>(null);
        return Task.FromResult<ContextSection?>(new ContextSection(Key, string.Join("\n\n", entries)));
    }

    /// <summary>Resolve and render one reference as a file-content or directory-listing entry.</summary>
    private string RenderResolvedReference(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = ResolveWithinRoot(reference);
        if (Directory.Exists(absolute))
        {
            var names = Directory.EnumerateFileSystemEntries(absolute)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Take(_maxEntriesPerDirectory)
                .Select(path => Directory.Exists(path) ? Path.GetFileName(path) + "/" : Path.GetFileName(path));
            return $"File reference: {reference}/\n\n{string.Join("\n", names)}";
        }
        if (File.Exists(absolute))
        {
            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new FileReferenceError(reference, $"cannot be read: {error.Message}");
            }
            var truncated = content.Length > _maxBytesPerFile
                ? content[.._maxBytesPerFile] + "\n… (truncated)"
                : content;
            return $"File reference: {reference}\n\n{truncated}";
        }
        throw new FileReferenceError(reference, "does not exist within the workspace root");
    }

    /// <summary>Resolve a mention to an absolute path inside the workspace root, failing loud outside.</summary>
    private string ResolveWithinRoot(string reference)
    {
        var normalized = reference.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            throw new FileReferenceError(reference, "must not be empty");
        }
        // Absolute paths and drive-qualified forms are outside the workspace root by definition.
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new FileReferenceError(reference, "is an absolute or drive-qualified path outside the workspace root");
        }
        var segments = normalized.Split('/');
        if (segments.Contains(".."))
        {
            throw new FileReferenceError(reference, "escapes the workspace root (\"..\" traversal)");
        }
        var combined = Path.GetFullPath(Path.Combine(_workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinRoot(combined))
        {
            throw new FileReferenceError(reference, "resolves outside the workspace root");
        }
        return combined;
    }

    /// <summary>Whether a fully resolved path equals the root or sits under it.</summary>
    private bool IsWithinRoot(string path)
    {
        var root = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar);
        var candidate = path.TrimEnd(Path.DirectorySeparatorChar);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
