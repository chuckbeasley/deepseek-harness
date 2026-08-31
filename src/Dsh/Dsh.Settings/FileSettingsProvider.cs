using System.Text.Json;
using Cordis.Core;

namespace Dsh.Settings;

/// <summary>
/// File-backed settings provider: one JSON document carries every namespace section. The document
/// loads and publishes on service attach and every write commits the whole document back to the
/// file. The C# port is JSON-only and has no hot-reload watcher (the TS provider also accepts YAML
/// and hot-publishes external edits).
/// </summary>
public sealed class FileSettingsProvider : SettingsProvider
{
    private readonly string _path;
    private Dictionary<string, object?>? _document;

    /// <summary>
    /// Create the provider over one JSON document.
    /// </summary>
    /// <param name="ctx">The context the provider registers in.</param>
    /// <param name="path">Absolute or relative JSON document path; a non-<c>.json</c> extension fails loud.</param>
    /// <exception cref="ArgumentException">when the path extension is not <c>.json</c>.</exception>
    public FileSettingsProvider(Context ctx, string path)
        : base(ctx)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = Path.GetFullPath(path);
        var extension = Path.GetExtension(_path).ToLowerInvariant();
        if (extension != ".json")
        {
            throw new ArgumentException($"settings-file: extension \"{extension}\" is not supported (use .json)", nameof(path));
        }
    }

    /// <summary>The local document is always writable through update and replace.</summary>
    public override bool Writable => true;

    /// <summary>The resolved JSON document path exposed to local configuration surfaces.</summary>
    public override string? DocumentPath => _path;

    /// <summary>Materialize an absent document as an empty JSON object and return the resolved path.</summary>
    public override string? PrepareDocument()
    {
        if (!File.Exists(_path))
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_path, "{}\n");
        }
        return _path;
    }

    /// <summary>Load the stored document; an absent file is the empty document, an unparsable one fails loud.</summary>
    protected override Task<Dictionary<string, object?>> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            _document = null;
            return Task.FromResult(new Dictionary<string, object?>());
        }
        var text = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text))
        {
            _document = new Dictionary<string, object?>();
            return Task.FromResult(_document);
        }
        object? root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = SettingsJson.FromElement(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"settings-file: invalid document at {_path}: {error.Message}", error);
        }
        if (root is not Dictionary<string, object?> sections)
        {
            throw new ArgumentException($"settings-file: {_path} must be a map of namespace sections");
        }
        _document = sections;
        return Task.FromResult(sections);
    }

    /// <summary>Fold one namespace section into the document mirror and write the whole document.</summary>
    protected override Task PersistAsync(SettingsNamespace ns, Dictionary<string, object?> section)
    {
        _document ??= new Dictionary<string, object?>();
        _document[ns.Value] = section;
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(_path, JsonSerializer.Serialize(_document, SerializerOptions));
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
