using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cordis.Core;

namespace Dsh.Spill;

/// <summary>Configuration for the local spill backend: the root directory holding every spill file.</summary>
/// <remarks>
/// <c>Root</c> has NO default on purpose: the TS spill-local falls back to a lazily-created private
/// per-process temp directory; the port requires the location explicitly so spill files never land
/// in an undiscoverable default (mirrors the storage-json root policy).
/// </remarks>
public sealed record SpillProviderConfig(string Root);

/// <summary>
/// Local spill provider for ctx.spill (port of packages/spill/spill-local): files land under
/// <c>&lt;root&gt;/session-&lt;sha256-12&gt;/…</c> with unpredictable names (12 random hex chars
/// plus the encoded suggested name) written with an exclusive create, so a spilled tool result is
/// never clobbered and a planted name can never traverse out of the root.
///
/// Deviations from spill-local: the TS launches a one-shot age-based startup sweep and keeps files
/// for a retention period so resumed sessions can still read old locators; the port's provider owns
/// the lifecycle of the files it registered — <see cref="ISpillService.Cleanup"/> runs the
/// age-based sweep on demand, and <see cref="StopAsync"/> deletes every registered spill file so
/// provider teardown leaves no residue. The spill-policy seam (what the model sees when a result is
/// spilled) is deferred and named here.
/// </summary>
public sealed class LocalSpillProvider : Service, ISpillService
{
    private static readonly Regex SessionDirRegex = new("^session-[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly List<SpillFile> _registered = new();

    /// <summary>Register the provider as ctx.spill over <paramref name="config"/>.Root; the root directory is created when missing.</summary>
    public LocalSpillProvider(Context ctx, SpillProviderConfig config)
        : base(ctx, "spill")
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Root))
        {
            throw new ArgumentException("spill root must be a non-empty path", nameof(config));
        }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root));
        Directory.CreateDirectory(_root);
    }

    /// <summary>The normalized spill root every registered path lives inside.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public SpillFile Claim(string sessionId, string suggestedName, string content)
    {
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        var encoded = EncodeSegment(suggestedName);
        var full = Path.Combine(dir, $"{RandomHex(6)}-{encoded}");
        var attempts = 0;
        for (;;)
        {
            try
            {
                using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                break;
            }
            catch (IOException) when (File.Exists(full) && attempts < 32)
            {
                // A name collision (the 6 random bytes repeat) — retry with a fresh name.
                full = Path.Combine(dir, $"{RandomHex(6)}-{encoded}");
                attempts++;
            }
        }
        var spill = new SpillFile(full, new FileInfo(full).Length);
        _registered.Add(spill);
        return spill;
    }

    /// <inheritdoc />
    public SpillFile Register(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SpillError("cannot register an empty spill path", SpillErrorCodes.InvalidPath);
        }
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SpillError($"cannot resolve \"{path}\": invalid path", SpillErrorCodes.InvalidPath, error);
        }
        if (!IsInsideRoot(full))
        {
            throw new SpillError($"path \"{path}\" escapes the spill root \"{_root}\"", SpillErrorCodes.OutsideRoot);
        }
        if (!File.Exists(full))
        {
            throw new SpillError($"cannot register \"{path}\": not found", SpillErrorCodes.NotFound);
        }
        if (FindRegistration(full) is not null)
        {
            throw new SpillError($"spill path \"{full}\" is already registered", SpillErrorCodes.AlreadyRegistered);
        }
        var spill = new SpillFile(full, new FileInfo(full).Length);
        _registered.Add(spill);
        return spill;
    }

    /// <inheritdoc />
    public bool Release(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unresolvable path has no registration and no file to delete: release is a no-op.
            return false;
        }
        var index = _registered.FindIndex(file => PathEquals(file.Path, full));
        var wasRegistered = index >= 0;
        if (wasRegistered)
        {
            _registered.RemoveAt(index);
        }
        if (File.Exists(full))
        {
            try
            {
                File.Delete(full);
            }
            catch (Exception error)
            {
                throw new SpillError($"cannot release \"{full}\": {error.Message}", SpillErrorCodes.IoError, error);
            }
        }
        return wasRegistered;
    }

    /// <inheritdoc />
    public IReadOnlyList<SpillFile> List() => _registered.ToList();

    /// <inheritdoc />
    public void Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                if (!SessionDirRegex.IsMatch(Path.GetFileName(dir) ?? string.Empty)) continue;
                SweepSessionDir(dir, cutoff);
            }
        }
        catch (Exception)
        {
            // Contained: the sweep is best-effort and never rejects (mirrors sweepSpillRoots).
        }
        _registered.RemoveAll(file => !File.Exists(file.Path));
    }

    /// <summary>Delete every registered spill file during provider teardown so no residue remains.</summary>
    public override ValueTask StopAsync()
    {
        foreach (var file in _registered.ToArray())
        {
            try
            {
                if (File.Exists(file.Path)) File.Delete(file.Path);
            }
            catch (Exception)
            {
                // Contained: teardown cleanup is best-effort; the file may already be gone.
            }
        }
        _registered.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Encode an arbitrary string as one safe path segment, injectively over all UTF-16 strings
    /// (port of spill-local store.encodeSegment). A session id / suggested name is untrusted input,
    /// so this neutralizes <c>../</c>, absolute paths, NUL, and separators before any filesystem
    /// use. Each code unit is kept literal (<c>[A-Za-z0-9._-]</c>, minus <c>~</c>) or escaped as
    /// <c>~XXXX</c>; <c>~</c> is itself escaped, so the mapping is reversible and distinct inputs
    /// never collide. The whole-segment tokens <c>.</c>/<c>..</c> are escaped so they can never
    /// traverse. An empty string encodes to <c>~</c> (never an empty segment).
    /// </summary>
    /// <param name="raw">untrusted text.</param>
    /// <returns>one injective filesystem-safe path segment.</returns>
    public static string EncodeSegment(string raw)
    {
        if (raw.Length == 0) return "~";
        if (raw == ".") return "~002E";
        if (raw == "..") return "~002E~002E";
        var builder = new StringBuilder(raw.Length * 5);
        foreach (var ch in raw)
        {
            var literal = ch != '~'
                && ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                    || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-');
            if (literal)
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('~').Append(((int)ch).ToString("X4"));
            }
        }
        return builder.ToString();
    }

    /// <summary>Derive the stable session-scoped directory under the root (port of spill-local store.sessionDir).</summary>
    private string SessionDir(string sessionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant();
        return Path.Combine(_root, $"session-{hash.Substring(0, 12)}");
    }

    private static string RandomHex(int byteCount)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private SpillFile? FindRegistration(string fullPath)
    {
        foreach (var file in _registered)
        {
            if (PathEquals(file.Path, fullPath)) return file;
        }
        return null;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private bool IsInsideRoot(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    /// <summary>Delete expired regular files in one session directory and prune it when empty; never follows symlinks.</summary>
    private void SweepSessionDir(string dir, DateTime cutoff)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception)
                        {
                            // Contained: one unreadable file does not abort the directory sweep.
                        }
                    }
                }
                catch (Exception)
                {
                    // Contained: an entry that raced away or faults on stat is skipped.
                }
            }
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try
                {
                    Directory.Delete(dir);
                }
                catch (Exception)
                {
                    // Contained: a concurrent writer may have refilled the directory.
                }
            }
        }
        catch (Exception)
        {
            // Contained: the session directory itself may be unreadable or racing away.
        }
    }
}
