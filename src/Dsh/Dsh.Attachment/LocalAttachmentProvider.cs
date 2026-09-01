using System.Security.Cryptography;
using Cordis.Core;

namespace Dsh.Attachment;

/// <summary>Configuration for the local attachment backend: the root holding one object file per attachment plus the byte limit.</summary>
/// <remarks>
/// <c>MaxBytes</c> has no default on purpose: the admission size is a deployment-resolved policy
/// (the TS resolves it from ImageAttachmentLimits), so assemblies state it explicitly.
/// </remarks>
public sealed record AttachmentProviderConfig(string Root, long MaxBytes);

/// <summary>
/// Local attachment provider for ctx.attachment (port of the ingest/list/read/remove core of
/// packages/attachment/attachment-local): each ingested file is copied to a fresh object file at
/// <c>&lt;root&gt;/&lt;id&gt;</c> (a generated uuid) via a same-directory temp file, so a
/// partially-copied object is never observable at the target path. Display names are stripped of
/// both separator styles and control characters before they can leak local path information.
///
/// Deferred seams, named here: image admission (media-type verification, pixel/dimension limits),
/// normalization (downscaling), content-addressed <c>sha256:</c> ids with digest verification, and
/// model-request image projection are not ported in this phase. Unlike spill files, attachments are
/// durable content: provider teardown deletes nothing.
/// </summary>
public sealed class LocalAttachmentProvider : Service, IAttachmentService
{
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly Dictionary<AttachmentId, AttachmentRef> _registered = new();

    /// <summary>Register the provider as ctx.attachment over <paramref name="config"/>.Root; the root directory is created when missing.</summary>
    public LocalAttachmentProvider(Context ctx, AttachmentProviderConfig config)
        : base(ctx, "attachment")
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Root))
        {
            throw new ArgumentException("attachment root must be a non-empty path", nameof(config));
        }
        if (config.MaxBytes < 1)
        {
            throw new ArgumentException("maxBytes must be a positive integer", nameof(config));
        }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root));
        _maxBytes = config.MaxBytes;
        Directory.CreateDirectory(_root);
    }

    /// <summary>The normalized attachment root holding one object file per id.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public AttachmentRef Ingest(string sourcePath, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new AttachmentError("cannot ingest an empty source path", AttachmentErrorCodes.NotFound);
        }
        string source;
        try
        {
            source = Path.GetFullPath(sourcePath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AttachmentError($"cannot resolve \"{sourcePath}\": invalid path", AttachmentErrorCodes.NotFound, error);
        }
        if (Directory.Exists(source))
        {
            throw new AttachmentError($"cannot ingest \"{sourcePath}\": path is a directory", AttachmentErrorCodes.NotRegularFile);
        }
        if (!File.Exists(source))
        {
            throw new AttachmentError($"cannot ingest \"{sourcePath}\": not found", AttachmentErrorCodes.NotFound);
        }
        var length = new FileInfo(source).Length;
        if (length > _maxBytes)
        {
            throw new AttachmentError(
                $"cannot ingest \"{sourcePath}\": {length} bytes exceeds the {_maxBytes}-byte limit",
                AttachmentErrorCodes.TooLarge);
        }

        var id = new AttachmentId(Guid.NewGuid().ToString("N"));
        var display = SanitizeName(name ?? Path.GetFileName(source)) ?? id.Value;
        var target = Path.Combine(_root, id.Value);
        var temp = Path.Combine(_root, $".{id.Value}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temp);
            File.Move(temp, target, overwrite: false);
        }
        catch (Exception error)
        {
            TryDeleteQuietly(temp);
            throw new AttachmentError($"cannot ingest \"{sourcePath}\": {error.Message}", AttachmentErrorCodes.WriteFailed, error);
        }
        var reference = new AttachmentRef(id, display, length);
        _registered[id] = reference;
        return reference;
    }

    /// <inheritdoc />
    public IReadOnlyList<AttachmentRef> List() => _registered.Values.ToList();

    /// <inheritdoc />
    public AttachmentData Read(AttachmentId id)
    {
        var path = ObjectPath(id);
        if (!File.Exists(path))
        {
            throw new AttachmentError($"attachment '{id}' object is missing", AttachmentErrorCodes.NotFound);
        }
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception error)
        {
            throw new AttachmentError($"cannot read attachment '{id}': {error.Message}", AttachmentErrorCodes.ReadFailed, error);
        }
        // A persisted object read back after a restart has no registry entry; the id doubles as the name.
        var reference = _registered.TryGetValue(id, out var known)
            ? known
            : new AttachmentRef(id, id.Value, content.Length);
        if (reference.Bytes != content.Length)
        {
            throw new AttachmentError($"attachment '{id}' failed integrity verification", AttachmentErrorCodes.Corrupt);
        }
        return new AttachmentData(reference, content);
    }

    /// <inheritdoc />
    public bool Remove(AttachmentId id)
    {
        var path = ObjectPath(id);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error)
            {
                throw new AttachmentError($"cannot remove attachment '{id}': {error.Message}", AttachmentErrorCodes.WriteFailed, error);
            }
        }
        return _registered.Remove(id);
    }

    /// <inheritdoc />
    public Dsh.Llm.ImageAttachment SaveImage(byte[] data, string mediaType, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > _maxBytes)
        {
            throw new AttachmentError(
                $"cannot save image: {data.Length} bytes exceeds the {_maxBytes}-byte limit", AttachmentErrorCodes.TooLarge);
        }
        var facts = ImageFactsParser.Parse(data, mediaType);
        // Content-addressed id: the recorded corpus references sha256: ids, and identical bytes
        // always land on the same object (idempotent concurrent saves cannot conflict).
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var objectPath = Path.Combine(_root, hash);
        if (!File.Exists(objectPath))
        {
            var temp = Path.Combine(_root, $".{hash}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, objectPath, overwrite: false);
            }
            catch (Exception error)
            {
                TryDeleteQuietly(temp);
                throw new AttachmentError($"cannot save image: {error.Message}", AttachmentErrorCodes.WriteFailed, error);
            }
        }
        var display = SanitizeName(name) ?? $"sha256:{hash}";
        return new Dsh.Llm.ImageAttachment($"sha256:{hash}", facts.MediaType, data.Length, facts.Width, facts.Height, display);
    }

    /// <summary>The object file path for one attachment id.</summary>
    private string ObjectPath(AttachmentId id) => Path.Combine(_root, id.Value);

    /// <summary>
    /// Strip both separator styles and control characters from a display name (port of
    /// attachment-local store.displayName): a POSIX host treats <c>\</c> as an ordinary character,
    /// so a naive basename would keep a Windows client's full local path and leak it into the
    /// reference. Returns <c>null</c> when nothing usable remains.
    /// </summary>
    private static string? SanitizeName(string value)
    {
        var leaf = value;
        var separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (separator >= 0)
        {
            leaf = value.Substring(separator + 1);
        }
        var cleaned = new string(leaf.Where(ch => ch >= 0x20 && ch != 0x7f).ToArray()).Trim();
        if (cleaned.Length > 255)
        {
            cleaned = cleaned.Substring(0, 255);
        }
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static void TryDeleteQuietly(string path)
    {
        // The staged temp is owner-only residue; losing it cannot fail a committed ingest.
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Swallow: cleanup is best-effort by design.
        }
    }
}
