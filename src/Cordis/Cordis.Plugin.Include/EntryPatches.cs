using Cordis.Plugin.Loader;

namespace Cordis.Plugin.Include;

/// <summary>Parsed form of one runtime patch layer row (port of the vendored PatchOptions).</summary>
public sealed class EntryPatchOptions
{
    /// <summary>Target row id; required for non-insert patches.</summary>
    public string? Id { get; set; }

    /// <summary>Rows inserted into the target group (or the root list when no id).</summary>
    public List<object?>? Insert { get; set; }

    /// <summary>Expected row name; a mismatch skips the patch with a warning.</summary>
    public string? Name { get; set; }

    /// <summary>Replacement config.</summary>
    public object? Config { get; set; }

    /// <summary>Replacement group marker.</summary>
    public bool? Group { get; set; }

    /// <summary>Replacement disabled marker.</summary>
    public bool? Disabled { get; set; }

    /// <summary>Replacement inject list.</summary>
    public IReadOnlyList<string>? Inject { get; set; }

    /// <summary>True when the patch row carries a config key, even when the value is null.</summary>
    public bool HasConfig { get; set; }
}

/// <summary>
/// Port of the vendored <c>applyEntryPatches</c> and the row conversion feeding it. Entry rows are
/// plain dictionaries until conversion to <see cref="EntryOptions"/>; <c>!!js</c> values evaluate
/// through the restricted <see cref="ConfigExpression"/> language at conversion time (the vendored
/// loader resolves them lazily per entry fiber; the port evaluates once per apply because
/// Cordis.Core keeps one fiber per context — documented deviation).
/// </summary>
public static class EntryPatches
{
    /// <summary>
    /// Apply patch layers to an entry list. The input is never mutated and the result is always
    /// detached. Inserted entries are indexed as they are added, so a later patch in the same list
    /// can target a row an earlier patch inserted. A patch that matches nothing warns and skips.
    /// </summary>
    public static List<object?> Apply(List<object?> data, IReadOnlyList<object?>? patches, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = Clone(data);
        if (patches is null || patches.Count == 0) return result;

        var entryMap = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        BuildMap(result, entryMap);

        foreach (var rawPatch in patches)
        {
            var patch = ParsePatch(rawPatch, warn);
            if (patch is null) continue;

            if (patch.Insert is not null)
            {
                var inserts = patch.Insert;
                var clones = Clone(inserts);
                if (patch.Id is not null)
                {
                    if (!entryMap.TryGetValue(patch.Id, out var target))
                    {
                        warn($"patch insert: entry '{patch.Id}' not found");
                        continue;
                    }
                    if (target.TryGetValue("group", out var groupValue) && groupValue is not true)
                    {
                        warn($"patch insert: entry '{patch.Id}' is not a group");
                        continue;
                    }
                    if (target.TryGetValue("config", out var configValue) && configValue is not List<object?>)
                    {
                        if (configValue is not null)
                        {
                            warn($"patch insert: entry '{patch.Id}' config is not a list");
                            continue;
                        }
                    }
                    target["config"] = (target.TryGetValue("config", out var existing) ? existing : null) is List<object?> list
                        ? list.Concat(clones).ToList()
                        : clones;
                }
                else
                {
                    result.AddRange(clones);
                }
                // Index the applied clones so a later patch in the same list can target them.
                BuildMap(clones, entryMap);
                continue;
            }

            if (patch.Id is null)
            {
                warn("patch: id is required for non-insert patches");
                continue;
            }
            if (!entryMap.TryGetValue(patch.Id, out var row))
            {
                warn($"patch: entry '{patch.Id}' not found");
                continue;
            }
            if (patch.Name is not null && (!row.TryGetValue("name", out var rowName) || !Equals(rowName, patch.Name)))
            {
                warn($"patch: name mismatch for '{patch.Id}' (expected '{patch.Name}', got '{rowName}'), skipping");
                continue;
            }
            if (patch.HasConfig) row["config"] = Clone(patch.Config);
            if (patch.Group is not null) row["group"] = patch.Group;
            if (patch.Disabled is not null) row["disabled"] = patch.Disabled;
            if (patch.Inject is not null) row["inject"] = patch.Inject.ToList();
        }
        return result;
    }

    /// <summary>Convert one entry row dictionary into loader options, evaluating expressions.</summary>
    public static EntryOptions ToEntryOptions(Dictionary<string, object?> row, Action<string> warn)
    {
        var options = new EntryOptions();
        if (row.TryGetValue("id", out var idValue) && idValue is string id && id.Length > 0)
        {
            options.Id = id;
        }
        if (row.TryGetValue("name", out var nameValue) && nameValue is string name && name.Length > 0)
        {
            options.Name = name;
        }
        else
        {
            warn("entry row is missing a plugin name; the row will fail to import");
        }
        if (row.TryGetValue("group", out var groupValue) && groupValue is bool group)
        {
            options.Group = group;
        }
        if (row.TryGetValue("config", out var configValue))
        {
            options.Config = EvaluateDeep(configValue);
        }
        // Nested group rows mount their children through the loader GroupPlugin, which expects
        // EntryOptions lists; convert child row dictionaries recursively.
        if (options.Group == true && options.Config is List<object?> children)
        {
            options.Config = children.Select(child => child is Dictionary<string, object?> map
                ? ToEntryOptions(map, warn)
                : throw new InvalidOperationException("group child row must be a map")).ToList();
        }
        if (row.TryGetValue("disabled", out var disabledValue) && disabledValue is not null)
        {
            var evaluated = EvaluateDeep(disabledValue);
            options.Disabled = evaluated switch
            {
                bool boolean => boolean,
                string text when bool.TryParse(text, out var boolean) => boolean,
                null => false,
                _ => throw new ConfigExpressionException(
                    $"entry [{options.Id}] disabled value evaluated to [{evaluated}], expected a boolean"),
            };
        }
        if (row.TryGetValue("inject", out var injectValue) && injectValue is List<object?> injectList)
        {
            options.Inject = injectList.OfType<string>().ToList();
        }
        return options;
    }

    /// <summary>Evaluate every expression nested inside a config value, preserving the shape.</summary>
    public static object? EvaluateDeep(object? value) => value switch
    {
        ConfigExpression expression => expression.Evaluate(),
        Dictionary<string, object?> map => map.ToDictionary(
            pair => pair.Key,
            pair => EvaluateDeep(pair.Value),
            StringComparer.Ordinal),
        List<object?> list => list.Select(EvaluateDeep).ToList(),
        _ => value,
    };

    private static EntryPatchOptions? ParsePatch(object? rawPatch, Action<string> warn)
    {
        if (rawPatch is not Dictionary<string, object?> patch)
        {
            warn("patch: expected a map, skipped");
            return null;
        }
        var options = new EntryPatchOptions();
        if (patch.TryGetValue("id", out var idValue) && idValue is string id) options.Id = id;
        if (patch.TryGetValue("name", out var nameValue) && nameValue is string name) options.Name = name;
        if (patch.TryGetValue("insert", out var insertValue) && insertValue is List<object?> insert)
        {
            options.Insert = insert;
        }
        if (patch.TryGetValue("config", out var configValue))
        {
            options.Config = configValue;
            options.HasConfig = true;
        }
        if (patch.TryGetValue("group", out var groupValue) && groupValue is bool group) options.Group = group;
        if (patch.TryGetValue("disabled", out var disabledValue) && disabledValue is bool disabled) options.Disabled = disabled;
        if (patch.TryGetValue("inject", out var injectValue) && injectValue is List<object?> injectList)
        {
            options.Inject = injectList.OfType<string>().ToList();
        }
        return options;
    }

    private static void BuildMap(List<object?> entries, Dictionary<string, Dictionary<string, object?>> entryMap)
    {
        foreach (var entry in entries)
        {
            if (entry is not Dictionary<string, object?> row) continue;
            if (row.TryGetValue("id", out var idValue) && idValue is string id && id.Length > 0)
            {
                entryMap[id] = row;
            }
            if (row.TryGetValue("group", out var groupValue) && groupValue is true &&
                row.TryGetValue("config", out var configValue) && configValue is List<object?> children)
            {
                BuildMap(children, entryMap);
            }
        }
    }

    /// <summary>Deep-clone a config value (lists and dictionaries; scalars are immutable).</summary>
    public static object? Clone(object? value) => value switch
    {
        Dictionary<string, object?> map => map.ToDictionary(
            pair => pair.Key,
            pair => Clone(pair.Value),
            StringComparer.Ordinal),
        List<object?> list => list.Select(Clone).ToList(),
        _ => value,
    };

    /// <summary>Deep-clone an entry list with the list type preserved.</summary>
    public static List<object?> Clone(List<object?> list) => (List<object?>)Clone((object?)list)!;
}
