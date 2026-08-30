using System.Globalization;
using System.Text.Json;
using Cordis.Plugin.Include;

namespace Dsh.Cli;

/// <summary>One overlay patch list with the source label printed in dump comments.</summary>
public sealed record ConfigDumpLayer(string Label, List<object?> Patches);

/// <summary>
/// Config-dump entry for <c>dsh --profile &lt;name&gt; --dump-config</c> (port of
/// <c>apps/cli/src/dump-config.ts</c> and <c>renderConfigDump</c>): compose the profile's patch
/// layers through the Include port's patch algorithm without booting or evaluating <c>!!js</c>,
/// with one source layer per bundle, the profile's own patch file, and each <c>--patch</c>
/// overlay, then render the result as YAML with a comment naming each source file and any layers
/// that patched its rows.
/// </summary>
public static class DumpConfig
{
    private const string Name = "dsh";

    /// <summary>
    /// Print a profile composition with comments naming each source file and patch layer.
    /// </summary>
    /// <param name="profile">the profile name.</param>
    /// <param name="defaultOnly">omit the profile's user layer and <c>--patch</c> overlays
    /// (the recovery diagnostic for a broken <c>cordis.patch.yml</c>, which is then never parsed).</param>
    /// <param name="patchFiles"><c>--patch</c> overlay paths, in argv order.</param>
    public static void RunDumpConfig(string profile, bool defaultOnly, IReadOnlyList<string> patchFiles)
    {
        var loaded = ProfileBoot.PrepareProfile(profile, !defaultOnly);
        var layers = new List<ConfigDumpLayer>();
        foreach (var layer in loaded.Layers)
        {
            layers.Add(new ConfigDumpLayer(layer.PackageName, layer.Patches));
        }
        if (!defaultOnly)
        {
            if (File.Exists(loaded.PatchPath))
            {
                layers.Add(new ConfigDumpLayer(loaded.PatchPath, loaded.Patches));
            }
            var homePatchFile = ProfileBoot.HomePatchPath();
            var homePatches = ProfileBoot.LoadOptionalPatches(homePatchFile);
            if (homePatches is not null)
            {
                layers.Add(new ConfigDumpLayer(homePatchFile, homePatches));
            }
            foreach (var file in patchFiles)
            {
                var absolute = Path.GetFullPath(file);
                layers.Add(new ConfigDumpLayer(absolute, ProfileBoot.LoadOverlayPatches(absolute)));
            }
        }
        // The dump anchors on the same empty root file the boot includes.
        var rootPath = Path.Combine(loaded.Dir, ProfileBoot.ProfileRootFilename);
        Console.Out.Write(Render(rootPath, layers, line => Console.Error.WriteLine(line)));
    }

    /// <summary>
    /// Compose the effective entry list exactly as boot would mount it: apply every layer's
    /// patches as one flattened list through the Include port's patch algorithm (the same single
    /// call boot makes, so even patch-visibility corner cases compose identically), then render
    /// the result grouped under one <c># ==</c> comment per contiguous source run, naming the
    /// layers that patched each run.
    /// </summary>
    /// <param name="configPath">the base config file boot would include.</param>
    /// <param name="layers">overlay layers in application order (later wins).</param>
    /// <param name="warn">sink for skipped-patch diagnostics; defaults to stderr.</param>
    /// <returns>the composed entry list rendered as a YAML document with source comment separators.</returns>
    public static string Render(string configPath, IReadOnlyList<ConfigDumpLayer> layers, Action<string>? warn = null)
    {
        var baseList = ProfileBoot.ParseEntryList(configPath);
        var baseLabel = Path.GetFileName(configPath);
        // snapshot_k = ONE application of layers 1..k flattened, using the exact arguments boot
        // passes for that prefix. snapshot_N is the mounted composition. The patches are cloned
        // per call: applyEntryPatches detaches the entry list but pushes insert rows by reference
        // from the patch list, so sharing patch objects across snapshot calls would leak a later
        // snapshot's mutations into an earlier one's result.
        var provenance = baseList.Select(_ => new Provenance(baseLabel)).ToList();
        var previous = baseList;
        var previousWarnings = 0;
        var composed = baseList;
        for (var count = 1; count <= layers.Count; count++)
        {
            var layer = layers[count - 1];
            var warnings = new List<string>();
            var flat = layers.Take(count).SelectMany(candidate => candidate.Patches).Select(EntryPatches.Clone).ToList();
            composed = EntryPatches.Apply(baseList, flat, warnings.Add);
            for (var index = previousWarnings; index < warnings.Count; index++)
            {
                warn?.Invoke($"{Name}: [{layer.Label}] {warnings[index]}");
            }
            var before = previous.Select(Signature).ToArray();
            for (var index = 0; index < composed.Count; index++)
            {
                if (index >= before.Length) provenance.Add(new Provenance(layer.Label));
                else if (Signature(composed[index]) != before[index]) provenance[index].PatchedBy.Add(layer.Label);
            }
            previous = composed;
            previousWarnings = warnings.Count;
        }
        return GroupedDump(composed, provenance);
    }

    /// <summary>Render the composed rows grouped under one source-and-patches comment per contiguous run.</summary>
    private static string GroupedDump(List<object?> composed, List<Provenance> provenance)
    {
        var lines = new List<string>();
        string? currentLabel = null;
        var group = new List<object?>();
        void Flush()
        {
            if (currentLabel is null || group.Count == 0) return;
            lines.Add($"# == {currentLabel}");
            lines.Add(Include.RenderEntryList(group).TrimEnd('\n'));
            group.Clear();
        }
        for (var index = 0; index < composed.Count; index++)
        {
            var record = provenance[index];
            var label = record.PatchedBy.Count == 0
                ? record.Origin
                : $"{record.Origin}, patched by {string.Join(", ", record.PatchedBy)}";
            if (label != currentLabel)
            {
                Flush();
                currentLabel = label;
            }
            group.Add(composed[index]);
        }
        Flush();
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Stable canonical row signature for the positional provenance diff (JSON.stringify semantics).</summary>
    private static string Signature(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        string text => Quote(text),
        ConfigExpression expression => Quote("!!js " + expression.Source),
        List<object?> list => "[" + string.Join(",", list.Select(Signature)) + "]",
        Dictionary<string, object?> map => "{" + string.Join(",", map.Select(pair => Quote(pair.Key) + ":" + Signature(pair.Value))) + "}",
        _ => JsonSerializer.Serialize(value),
    };

    private static string Quote(string text)
        => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>One composed row's source run and the layers that patched it.</summary>
    private sealed class Provenance
    {
        public Provenance(string origin)
        {
            Origin = origin;
        }

        public string Origin { get; }

        public List<string> PatchedBy { get; } = new();
    }
}
