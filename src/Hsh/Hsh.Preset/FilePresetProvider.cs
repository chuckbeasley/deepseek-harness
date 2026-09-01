using System.Text.RegularExpressions;
using Harness.Cordis.Plugin.Include;
using Harness.Cordis.Plugin.Loader;

namespace Harness.Preset;

/// <summary>
/// Filesystem discovery and composition of agent presets (port of the TS discovery module plus
/// the Include-based composition). A preset is a directory holding <see cref="CompositionFile"/>,
/// whose name is the preset id; the composition is an entry-list YAML document, parsed with the
/// Include port's <see cref="YamlSubset"/> and composed through the Include patch API
/// (<see cref="EntryPatches"/>) so runtime patch layers apply exactly as they would at a loader
/// mount. Discovery re-reads the root on every call and reports broken presets rather than
/// skipping them; <see cref="Resolve"/> fails loud on a missing or unusable preset.
/// </summary>
public sealed class FilePresetProvider : IPresetService
{
    /// <summary>The composition file that makes a directory a preset.</summary>
    public const string CompositionFile = "agent.cordis.yml";

    /// <summary>Ids a preset directory may use: the id becomes a path segment, so this is a containment boundary.</summary>
    public static readonly Regex PresetId = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    private readonly string _root;
    private readonly IReadOnlyList<object?>? _patches;
    private readonly Action<string> _warn;
    private readonly PresetTrust _trust;

    /// <summary>
    /// Create the provider over one preset root.
    /// </summary>
    /// <param name="root">the directory holding one subdirectory per preset.</param>
    /// <param name="patches">runtime patch layers applied to every preset's composition after each
    /// read, in order (the Include patch dialect).</param>
    /// <param name="warn">receiver for patch warnings (a skipped patch, a missing target); absent
    /// warnings are discarded.</param>
    /// <param name="trust">trust recorded on every preset discovered under this root (the TS
    /// root trust; a <c>system</c> root refuses authoring).</param>
    public FilePresetProvider(string root, IReadOnlyList<object?>? patches = null, Action<string>? warn = null, PresetTrust trust = PresetTrust.User)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("preset root must not be empty", nameof(root));
        }
        _root = Path.GetFullPath(root);
        _patches = patches;
        _warn = warn ?? (_ => { });
        _trust = trust;
    }

    /// <summary>The absolute preset root this provider scans.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public IReadOnlyList<PresetInfo> Discover()
    {
        if (!Directory.Exists(_root)) return Array.Empty<PresetInfo>();
        var found = new List<PresetInfo>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            if (!PresetId.IsMatch(id)) continue;
            var path = Path.Combine(directory, CompositionFile);
            string? broken;
            if (!File.Exists(path))
            {
                broken = $"the composition file {CompositionFile} is missing — the directory still occupies the id; delete it or restore the file";
            }
            else
            {
                broken = CompositionProblem(path);
            }
            found.Add(new PresetInfo(id, path, broken, _trust));
        }
        return found.OrderBy(preset => preset.Id, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public ComposedPreset Resolve(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException("agent-presets: preset id must be a non-empty string");
        }
        var row = Discover().FirstOrDefault(preset => preset.Id == id)
            ?? throw new InvalidOperationException($"agent-presets: preset \"{id}\" not found under root {_root}");
        if (row.Broken is not null)
        {
            throw new InvalidOperationException($"agent-presets: preset \"{id}\" failed to mount: {row.Broken}");
        }
        var rows = Compose(row.CompositionPath);
        return new ComposedPreset(id, row.CompositionPath, rows, _trust);
    }

    /// <summary>
    /// Compose one composition file into loader rows: parse with the Include YAML dialect, apply
    /// the provider's patch layers, and convert every row through the Include conversion. Fail
    /// loud on any step — a composition that stopped reading as an entry list is an error here,
    /// never a silent empty list.
    /// </summary>
    /// <param name="path">absolute path of the composition file.</param>
    /// <returns>the composed plugin rows, in composition order.</returns>
    public IReadOnlyList<EntryOptions> Compose(string path)
    {
        var problem = CompositionProblem(path);
        if (problem is not null)
        {
            throw new InvalidOperationException($"agent-presets: preset \"{Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty)}\" failed to mount: {problem}");
        }
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"agent-presets: cannot read composition {path}: {error.Message}", error);
        }
        var parsed = YamlSubset.Parse(content);
        var patched = EntryPatches.Apply(parsed as List<object?>
            ?? throw new InvalidOperationException("agent-presets: the composition must be a top-level list of plugin rows"), _patches, _warn);
        var rows = new List<EntryOptions>(patched.Count);
        foreach (var row in patched)
        {
            if (row is not Dictionary<string, object?> map)
            {
                throw new InvalidOperationException("agent-presets: config row must be a map");
            }
            rows.Add(EntryPatches.ToEntryOptions(map, _warn));
        }
        return rows;
    }

    /// <summary>
    /// Why the composition at <paramref name="path"/> cannot mount, or null when it looks
    /// loadable. Parsed with the loader's own YAML dialect (<see cref="YamlSubset"/>), so health
    /// can never call a composition broken that the Include port would accept.
    /// </summary>
    public string? CompositionProblem(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception)
        {
            // The caller statted this file moments ago; any read failure now — deleted in
            // between, permissions — is the same answer as unparsable.
            return $"the composition file {CompositionFile} cannot be read";
        }
        object? rows;
        try
        {
            rows = YamlSubset.Parse(content);
        }
        catch (YamlParseException error)
        {
            // First line only: YamlSubset appends the offending line, and the reason is displayed
            // on a roster card, not in a terminal.
            return $"the composition is not valid YAML: {FirstLine(error.Message)}";
        }
        return EntryListProblem(rows);
    }

    /// <summary>
    /// Why <paramref name="rows"/> cannot be an entry list, or null when it can. A shallow shape
    /// check, deliberately short of the loader's work: rows are only required to be maps carrying
    /// a plugin <c>name</c> (groups recurse into their own lists).
    /// </summary>
    /// <param name="rows">the parsed composition document.</param>
    /// <param name="at">row-path prefix for nested diagnostics, empty at the top level.</param>
    /// <returns>one human-readable reason, or null when the shape holds.</returns>
    public static string? EntryListProblem(object? rows, string at = "")
    {
        if (rows is not List<object?> list)
        {
            return at.Length == 0
                ? "the composition must be a top-level list of plugin rows"
                : $"group {at} must hold a list of plugin rows";
        }
        for (var index = 0; index < list.Count; index++)
        {
            var label = at.Length == 0 ? $"row {index + 1}" : $"{at} row {index + 1}";
            if (list[index] is not Dictionary<string, object?> row)
            {
                return $"{label} is not a plugin row (expected a map with a \"name\")";
            }
            if (row.TryGetValue("name", out var nameValue) is false || nameValue is not string name || name.Length == 0)
            {
                return $"{label} names no plugin (a \"name\" string is required)";
            }
            if (row.TryGetValue("group", out var groupValue) && groupValue is true)
            {
                row.TryGetValue("config", out var configValue);
                var nested = EntryListProblem(configValue, label);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline];
    }
}
