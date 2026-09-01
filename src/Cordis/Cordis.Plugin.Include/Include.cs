using System.Globalization;
using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Cordis.Plugin.Loader;

namespace Harness.Cordis.Plugin.Include;

/// <summary>
/// File-backed loader entry tree (port of the vendored Include plugin). Reads an entry-list YAML or
/// JSON file, applies runtime patch layers, and reconciles the loader's root group transactionally.
/// <c>!!js</c> values evaluate through the restricted <see cref="ConfigExpression"/> language. Every
/// mutation runs through one serialized queue (the group update is not reentrant), and config
/// writes go through a retried temp-file rename. The vendored Include mounts a child entry tree;
/// the port composes the <see cref="Harness.Cordis.Plugin.Loader.Loader"/> and mounts rows on its root
/// group, because the loader's service accessor is assembly-internal (documented deviation). The
/// nested-group plugin ships with the Loader as <c>GroupPlugin</c>; this class owns the file half
/// of the seam.
/// </summary>
public sealed class Include
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yml", ".yaml", ".json",
    };

    private const int WriteRetryLimit = 10;
    private const int WriteRetryDelayMs = 50;

    private readonly Harness.Cordis.Plugin.Loader.Loader _loader;
    private readonly IncludeConfig _config;
    private string? _content;
    private Task _applyQueue = Task.CompletedTask;
    private Task _writeQueue = Task.CompletedTask;

    /// <summary>Create the include on <paramref name="ctx"/> for <paramref name="config"/>.</summary>
    public Include(Context ctx, IncludeConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _loader = ctx.Get<Harness.Cordis.Plugin.Loader.Loader>("loader")
            ?? throw new InvalidOperationException("the include requires the loader service");
        EnableLogs = config.EnableLogs;
        Filename = Path.GetFullPath(config.Path);
        if (!SupportedExtensions.Contains(Path.GetExtension(Filename)))
        {
            throw new InvalidOperationException($"extension \"{Path.GetExtension(Filename)}\" not supported");
        }
    }

    /// <summary>Absolute path of the entry-list file.</summary>
    public string Filename { get; }

    /// <summary>The include's config.</summary>
    public IncludeConfig Config => _config;

    /// <summary>Enables loader apply/reload/unload logs.</summary>
    public bool EnableLogs { get; set; }

    /// <summary>The current mounted rows (the loader root's data).</summary>
    public IReadOnlyList<EntryOptions> Rows => _loader.Root.Data;
    /// <summary>Render an entry list in the include's YAML dialect (used by the config dump).</summary>
    public static string RenderEntryList(IReadOnlyList<object?> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return string.Join("\n", data.Select(EmitListItem)) + "\n";
    }

    /// <summary>Read the file, apply patches, and mount the rows. Seeds the file on ENOENT.</summary>
    public Task ApplyFileAsync() => EnqueueAsync(() => ApplyCoreAsync(initialFallback: true));

    /// <summary>Re-read the file and refresh mounted rows when its content changed.</summary>
    public Task RefreshAsync() => EnqueueAsync(() => ApplyCoreAsync(initialFallback: false));

    /// <summary>Persist the current mounted rows back to the config file.</summary>
    public Task WriteAsync() => EnqueueWriteAsync(Rows.ToList());

    private Task EnqueueAsync(Func<Task> task)
    {
        var run = _applyQueue.ContinueWith(_ => task(), TaskScheduler.Default).Unwrap();
        _applyQueue = run.ContinueWith(_ => { }, TaskScheduler.Default);
        return run;
    }

    private Task EnqueueWriteAsync(IReadOnlyList<EntryOptions> rows)
    {
        var run = _writeQueue.ContinueWith(_ => WriteRowsAsync(rows), TaskScheduler.Default).Unwrap();
        _writeQueue = run.ContinueWith(_ => { }, TaskScheduler.Default);
        return run;
    }

    private async Task ApplyCoreAsync(bool initialFallback)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(Filename);
        }
        catch (FileNotFoundException) when (initialFallback && _config.Initial is not null)
        {
            await WriteDataAsync(_config.Initial);
            content = await File.ReadAllTextAsync(Filename);
        }
        if (content == _content) return;

        object? parsed = _config.IsJson ? YamlSubset.ParseJson(content) : YamlSubset.Parse(content);
        if (parsed is not List<object?> data)
        {
            throw new InvalidOperationException("config file must be a top-level array");
        }
        var patched = EntryPatches.Apply(data, _config.Patches, Warn);
        var rows = patched.Select(row => row is Dictionary<string, object?> map
            ? EntryPatches.ToEntryOptions(map, Warn)
            : throw new InvalidOperationException("config row must be a map")).ToList();
        await _loader.Root.UpdateAsync(rows);
        _content = content;
    }

    private void Warn(string message) => _loader.Ctx.Logger.Logger("loader").Warn(message);

    private async Task WriteRowsAsync(IReadOnlyList<EntryOptions> rows)
    {
        try
        {
            await WriteDataAsync(rows.Select(row => (object?)ToRowData(row)).ToList());
        }
        catch (Exception error)
        {
            Log.Warn($"failed to write config file {Filename}");
            Log.Warn(error.Message);
        }
    }

    private Harness.Cordis.Core.Logger Log => _loader.Ctx.Logger.Logger("loader");

    private async Task WriteDataAsync(List<object?> data)
    {
        var text = _config.IsJson
            ? JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })
            : string.Join("\n", data.Select(row => EmitListItem(row, 0))) + "\n";
        var temp = Filename + ".tmp";
        await File.WriteAllTextAsync(temp, text);
        for (var retry = 0; ; retry++)
        {
            try
            {
                File.Move(temp, Filename, overwrite: true);
                return;
            }
            catch (IOException) when (retry < WriteRetryLimit)
            {
                await Task.Delay((retry + 1) * WriteRetryDelayMs);
            }
        }
    }

    private static Dictionary<string, object?> ToRowData(EntryOptions options)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options.Id.Length > 0) row["id"] = options.Id;
        if (options.Name.Length > 0) row["name"] = options.Name;
        if (options.Config is not null) row["config"] = options.Config;
        if (options.Group is not null) row["group"] = options.Group;
        if (options.Disabled is not null) row["disabled"] = options.Disabled;
        if (options.Inject is not null) row["inject"] = options.Inject.ToList();
        return row;
    }

    private static string EmitListItem(object? item, int indent)
    {
        var pad = new string(' ', indent);
        if (item is Dictionary<string, object?> { Count: > 0 } map)
        {
            var first = map.First();
            var firstLine = IsContainer(first.Value)
                ? $"{pad}- {first.Key}:\n{EmitYaml(first.Value, indent + 2)}"
                : $"{pad}- {first.Key}: {EmitScalar(first.Value)}";
            var rest = string.Join("\n", map.Skip(1).Select(pair => IsContainer(pair.Value)
                ? $"{pad}  {pair.Key}:\n{EmitYaml(pair.Value, indent + 4)}"
                : $"{pad}  {pair.Key}: {EmitScalar(pair.Value)}"));
            return rest.Length == 0 ? firstLine : firstLine + "\n" + rest;
        }
        return IsContainer(item) ? $"{pad}-\n{EmitYaml(item, indent + 2)}" : $"{pad}- {EmitScalar(item)}";
    }

    private static string EmitYaml(object? value, int indent)
    {
        var pad = new string(' ', indent);
        return value switch
        {
            Dictionary<string, object?> map => string.Join("\n", map.Select(pair => IsContainer(pair.Value)
                ? $"{pad}{pair.Key}:\n{EmitYaml(pair.Value, indent + 2)}"
                : $"{pad}{pair.Key}: {EmitScalar(pair.Value)}")),
            List<object?> list => string.Join("\n", list.Select(item => EmitListItem(item, indent))),
            _ => pad + EmitScalar(value),
        };
    }

    private static bool IsContainer(object? value) => value is Dictionary<string, object?> { Count: > 0 }
        or List<object?> { Count: > 0 };

    private static string EmitScalar(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        ConfigExpression expression => "!!js " + expression.Source,
        string text when NeedsQuoting(text) => '"' + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"',
        string text => text,
        Dictionary<string, object?> => "{}",
        List<object?> => "[]",
        _ => JsonSerializer.Serialize(value),
    };

    private static bool NeedsQuoting(string text) =>
        text.Length == 0 ||
        char.IsWhiteSpace(text[0]) ||
        char.IsWhiteSpace(text[^1]) ||
        text.IndexOfAny(new[] { '#', ':', '-', '\n' }) >= 0 ||
        text is "null" or "true" or "false";
}

