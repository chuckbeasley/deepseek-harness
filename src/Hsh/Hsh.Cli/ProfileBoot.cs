using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Cordis.Core;
using Harness.Cordis.Cosmokit;
using Harness.Cordis.Plugin.Include;
using Harness.Cordis.Plugin.Loader;

namespace Harness.Cli;

/// <summary>One resolved bundle layer of a profile.</summary>
public sealed record ProfileLayer(string PackageName, string PackageDir, string PatchPath, List<object?> Patches);

/// <summary>A loaded profile: resolved bundle layers plus the user's own patch layer.</summary>
public sealed record Profile(
    string Name,
    string Dir,
    IReadOnlyList<ProfileLayer> Layers,
    string PatchPath,
    List<object?> Patches,
    string PatchReload);

/// <summary>One profile's patch layers, in application order.</summary>
public sealed record ComposedProfile(Profile Profile, List<object?> BundlePatches, List<object?> HomePatches, List<object?> Overlays)
{
    /// <summary>The full patch stack, in application order.</summary>
    public List<object?> AllPatches() => new List<object?>()
        .Concat(BundlePatches)
        .Concat(Profile.Patches)
        .Concat(HomePatches)
        .Concat(Overlays)
        .ToList();
}

/// <summary>The bundle half of the <c>hsh</c> manifest section: what a bundle exports.</summary>
public sealed class HshBundleManifest
{
    /// <summary>The patch layer this bundle exports, relative to its package root.</summary>
    public string? Patch { get; set; }
}

/// <summary>The profile half of the <c>hsh</c> manifest section: what a profile directory composes.</summary>
public sealed class HshProfileManifest
{
    /// <summary>Ordered bundle layer list (bundle names).</summary>
    public List<string>? Bundles { get; set; }

    /// <summary>Whether user patch files reload while this profile remains active ("live" or "startup").</summary>
    public string? PatchReload { get; set; }
}

/// <summary>The profile-launcher slice of the <c>hsh</c>-owned manifest section.</summary>
public sealed class HshManifestSection
{
    /// <summary>Bundle metadata consumed by the profile launcher.</summary>
    public HshBundleManifest? Bundle { get; set; }

    /// <summary>Profile metadata consumed by the profile launcher.</summary>
    public HshProfileManifest? Profile { get; set; }
}

/// <summary>
/// The C#-world profile manifest (deviation from the TS <c>package.json</c> slice
/// <c>hsh.profile</c>): the .NET port keeps one <c>profile.json</c> per profile and per bundle,
/// carrying the same <c>hsh.profile.bundles</c> / <c>hsh.bundle.patch</c> fields. The npm
/// package story (dependencies, pnpm-installed bundles) arrives with NuGet packaging in Phase 8.
/// </summary>
public sealed class ProfileManifest
{
    /// <summary>Profile display name (informational).</summary>
    public string? Name { get; set; }

    /// <summary>The <c>hsh</c> manifest section.</summary>
    public HshManifestSection? Hsh { get; set; }
}

/// <summary>Installation-owned defaults used when a shipped profile is first opened.</summary>
public sealed record ProfileTemplate(IReadOnlyList<string> Bundles, string PatchReload);

/// <summary>The invocation's inner arguments, provided to app plugins as the <c>cmdlineArgs</c> service.</summary>
public sealed record CmdlineArgs(IReadOnlyList<string> Args);

/// <summary>Bounded process-exit request, provided as the <c>appExit</c> service (port of hsh-cmdline's AppExit).</summary>
public sealed record AppExit(Action<int> Exit);

/// <summary>
/// Successful application-startup signal owned by the launcher (port of hsh-cmdline's AppReady):
/// listeners run once <see cref="Commit"/> is called after the tree settles.
/// </summary>
public sealed class AppReady
{
    private readonly object _gate = new();
    private readonly List<Action> _listeners = new();
    private bool _ready;

    /// <summary>Run <paramref name="listener"/> once successful startup is committed.</summary>
    /// <returns>a disposer that cancels a pending listener.</returns>
    public IDisposable OnReady(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            if (_ready)
            {
                listener();
                return new NoopDisposable();
            }
            _listeners.Add(listener);
            return new RemoveListener(this, listener);
        }
    }

    /// <summary>Commit successful startup and run every pending listener (listener failures are contained).</summary>
    internal void Commit()
    {
        Action[] pending;
        lock (_gate)
        {
            if (_ready) return;
            _ready = true;
            pending = _listeners.ToArray();
            _listeners.Clear();
        }
        foreach (var listener in pending)
        {
            try
            {
                listener();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"hsh: appReady listener threw: {error.Message}");
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class RemoveListener : IDisposable
    {
        private readonly AppReady _owner;
        private readonly Action _listener;

        public RemoveListener(AppReady owner, Action listener)
        {
            _owner = owner;
            _listener = listener;
        }

        public void Dispose()
        {
            lock (_owner._gate) _owner._listeners.Remove(_listener);
        }
    }
}

/// <summary>
/// Shared profile boot for every <c>hsh</c> surface (port of <c>apps/cli/src/profile-boot.ts</c>
/// and the profile slice of <c>@deepseek-ai/hsh-app-boot</c>): resolve the profile under
/// <c>$HSH_HOME/profiles/&lt;name&gt;</c>, rewrite its empty <c>cordis.yml</c> root, stack its patch
/// layers (bundle layers in <c>hsh.profile.bundles</c> order, the profile's own
/// <c>cordis.patch.yml</c>, the home-level <c>$HSH_HOME/cordis.patch.yml</c>, <c>--patch</c>
/// overlays), and mount the composed tree through the Loader port with the spine row registry.
/// </summary>
public static class ProfileBoot
{
    /// <summary>Directory under the Harness home holding every profile.</summary>
    public const string ProfilesDir = "profiles";

    /// <summary>The user patch layer inside a profile directory.</summary>
    public const string ProfilePatchFilename = "cordis.patch.yml";

    /// <summary>Root config filename inside a profile directory.</summary>
    public const string ProfileRootFilename = "cordis.yml";

    /// <summary>Manifest filename (deviation: the TS uses package.json; see <see cref="ProfileManifest"/>).</summary>
    public const string ProfileManifestFilename = "profile.json";

    /// <summary>Subdirectory under a bundle package root holding that bundle's manifest and patch.</summary>
    public const string BundlesDirName = "bundles";

    /// <summary>Environment variable that overrides the default harness home.</summary>
    public const string HshHomeEnv = "HSH_HOME";

    /// <summary>The session-telemetry row id the HSH_TELEMETRY_DISABLED switch targets.</summary>
    public const string TelemetryRowId = "session-telemetry-otel";

    /// <summary>Environment variable naming the telemetry opt-out switch.</summary>
    public const string TelemetryDisabledEnv = "HSH_TELEMETRY_DISABLED";

    /// <summary>The bundle list a <c>hsh plugin</c> init uses for a name with no shipped template.</summary>
    public static readonly IReadOnlyList<string> DefaultProfileBundles = new[] { "@deepseek-ai/hsh-base" };

    /// <summary>Custom profiles retain the historical live patch-file behavior.</summary>
    public const string DefaultProfilePatchReload = "live";

    /// <summary>The shipped profile templates auto-initialized on first use, by name.</summary>
    public static readonly IReadOnlyDictionary<string, ProfileTemplate> ProfileTemplates =
        new Dictionary<string, ProfileTemplate>(StringComparer.Ordinal)
        {
            ["headless"] = new(new[] { "@deepseek-ai/hsh-base", "@deepseek-ai/hsh-headless" }, "startup"),
            ["tui"] = new(new[] { "@deepseek-ai/hsh-base", "@deepseek-ai/hsh-tui" }, "live"),
            ["web"] = new(new[] { "@deepseek-ai/hsh-base", "@deepseek-ai/hsh-web" }, "live"),
            ["sdk"] = new(new[] { "@deepseek-ai/hsh-base", "@deepseek-ai/hsh-sdk" }, "startup"),
            ["acp"] = new(new[] { "@deepseek-ai/hsh-base", "@deepseek-ai/hsh-acp" }, "startup"),
        };

    /// <summary>The empty root entry list every profile tree patches over.</summary>
    public const string ProfileRootConfig = """
        # hsh profile root — an empty entry list. The tree is composed as patches:
        # each bundle in profile.json's hsh.profile.bundles, then cordis.patch.yml, then any
        # --patch overlays. Edit cordis.patch.yml, not this file.
        []
        """;

    private const string ProfilePatchTemplate = """
        # Your patch layer for this hsh profile, applied after every bundle layer:
        # a top-level YAML array of loader patch entries (id-targeted config
        # overrides, disables, and insert lists; !!js expressions allowed).
        []
        """;

    /// <summary>JSON options for profile and bundle manifests (camelCase keys, 2-space indent, case-insensitive reads).</summary>
    internal static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Resolve the harness home: <c>$HSH_HOME</c>, else the default <c>~/.hsh</c>.</summary>
    public static string ResolveHshHome() => HomePaths.ResolveHshHome();

    /// <summary>Resolve a profile's directory under the Harness home (may not exist yet).</summary>
    public static string ResolveProfileDir(string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name == "." || name == ".."
            || name == "node_modules")
        {
            throw new InvalidOperationException($"hsh: invalid profile name {JsonSerializer.Serialize(name)}");
        }
        return Path.Combine(ResolveHshHome(), ProfilesDir, name);
    }

    /// <summary>The home-level user patch layer (<c>$HSH_HOME/cordis.patch.yml</c>), applied over every profile's own layer.</summary>
    public static string HomePatchPath() => Path.Combine(ResolveHshHome(), ProfilePatchFilename);

    /// <summary>
    /// Initialize a profile directory: manifest and the empty user patch layer. Existing files are
    /// never touched, so re-running is a no-op on an initialized profile.
    /// </summary>
    public static void InitProfile(string dir, IReadOnlyList<string> bundles, string? patchReload = null)
    {
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, ProfileManifestFilename);
        if (!File.Exists(manifestPath))
        {
            var manifest = new ProfileManifest
            {
                Name = "hsh-profile-" + Path.GetFileName(dir),
                Hsh = new HshManifestSection
                {
                    Profile = new HshProfileManifest
                    {
                        Bundles = bundles.ToList(),
                        PatchReload = patchReload ?? DefaultProfilePatchReload,
                    },
                },
            };
            WriteProfileManifest(dir, manifest);
        }
        var patchPath = Path.Combine(dir, ProfilePatchFilename);
        if (!File.Exists(patchPath)) File.WriteAllText(patchPath, ProfilePatchTemplate);
    }

    /// <summary>Read a profile or bundle manifest from <paramref name="dir"/>.</summary>
    public static ProfileManifest ReadProfileManifest(string dir)
    {
        var path = Path.Combine(dir, ProfileManifestFilename);
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"hsh: failed to read profile manifest {path}: {error.Message}");
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<ProfileManifest>(raw, ManifestJson);
            if (parsed is null)
            {
                throw new InvalidOperationException($"hsh: profile manifest {path} must hold a JSON object");
            }
            return parsed;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"hsh: profile manifest {path} must hold a JSON object: {error.Message}");
        }
    }

    /// <summary>Write a profile's manifest back (2-space JSON, trailing newline).</summary>
    public static void WriteProfileManifest(string dir, ProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        File.WriteAllText(Path.Combine(dir, ProfileManifestFilename), JsonSerializer.Serialize(manifest, ManifestJson) + "\n");
    }

    /// <summary>
    /// Resolve one bundle's directory: the installation's in-box <c>bundles/</c> directory first,
    /// then the profile's own <c>bundles/</c> directory (the .NET equivalent of the TS
    /// installation-anchor-then-profile resolution; pnpm-managed installs arrive with Phase 8).
    /// </summary>
    public static string ResolveBundleDir(string bundleName, string profileDir)
    {
        foreach (var anchor in new[] { Path.Combine(AppContext.BaseDirectory, BundlesDirName), Path.Combine(profileDir, BundlesDirName) })
        {
            var candidate = Path.Combine(anchor, bundleName);
            if (File.Exists(Path.Combine(candidate, ProfileManifestFilename))) return candidate;
        }
        throw new InvalidOperationException(
            $"hsh: cannot resolve profile bundle {JsonSerializer.Serialize(bundleName)} from the hsh installation or {profileDir}; "
            + "place it under the installation's bundles directory or the profile's bundles directory");
    }

    /// <summary>
    /// Load a resolved profile for <paramref name="name"/> and (re)write the empty root config.
    /// The root is always rewritten: the whole composition is patch layers, and a tree write-back
    /// could bake composed rows into this file, duplicating every bundle insert on the next boot.
    /// </summary>
    /// <param name="name">the profile name.</param>
    /// <param name="userLayer"><c>false</c> skips parsing <c>cordis.patch.yml</c> (the default dump).</param>
    /// <returns>the loaded profile.</returns>
    public static Profile PrepareProfile(string name, bool userLayer = true)
    {
        var profile = LoadProfile(name, userLayer);
        File.WriteAllText(Path.Combine(profile.Dir, ProfileRootFilename), ProfileRootConfig);
        return profile;
    }

    /// <summary>
    /// Load a profile: resolve every <c>hsh.profile.bundles</c> entry to its patch layer and parse
    /// the profile's own patch file. A listed bundle without a <c>hsh.bundle</c> manifest fails
    /// loud — naming a bundle-less package as a layer is a misconfiguration, not "no patches".
    /// </summary>
    public static Profile LoadProfile(string name, bool userLayer = true)
    {
        var dir = ResolveProfileDir(name);
        if (!File.Exists(Path.Combine(dir, ProfileManifestFilename)))
        {
            if (!ProfileTemplates.TryGetValue(name, out var template))
            {
                throw new InvalidOperationException(
                    $"hsh: profile {JsonSerializer.Serialize(name)} does not exist; create it with 'hsh plugin --profile {name} add <package>'");
            }
            InitProfile(dir, template.Bundles, template.PatchReload);
        }
        var manifest = ReadProfileManifest(dir);
        var bundles = manifest.Hsh?.Profile?.Bundles ?? new List<string>();
        var rawPatchReload = manifest.Hsh?.Profile?.PatchReload;
        if (rawPatchReload is not null && rawPatchReload is not ("live" or "startup"))
        {
            throw new InvalidOperationException(
                $"hsh: profile manifest {Path.Combine(dir, ProfileManifestFilename)} hsh.profile.patchReload must be \"live\" or \"startup\"");
        }
        var patchReload = rawPatchReload ?? DefaultProfilePatchReload;
        var layers = bundles.Select(packageName =>
        {
            var packageDir = ResolveBundleDir(packageName, dir);
            var bundleManifest = ReadProfileManifest(packageDir);
            var declared = bundleManifest.Hsh?.Bundle?.Patch;
            if (declared is null)
            {
                throw new InvalidOperationException(
                    $"hsh: profile bundle {JsonSerializer.Serialize(packageName)} declares no hsh.bundle in its profile.json");
            }
            var patchPath = Path.Combine(packageDir, declared);
            return new ProfileLayer(packageName, packageDir, patchPath, LoadOverlayPatches(patchPath));
        }).ToList();
        var patchPath = Path.Combine(dir, ProfilePatchFilename);
        var patches = userLayer && File.Exists(patchPath) ? LoadOverlayPatches(patchPath) : new List<object?>();
        return new Profile(name, dir, layers, patchPath, patches, patchReload);
    }

    /// <summary>
    /// Load an optional patch-list file: a top-level YAML array of loader patch entries. A
    /// missing file means "no layer"; an unreadable, unparsable, or non-array file throws — a
    /// present patch file that cannot apply is a misconfiguration and must fail loud at boot.
    /// </summary>
    public static List<object?>? LoadOptionalPatches(string file)
    {
        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"hsh: failed to read patches {file}: {error.Message}");
        }
        return ParsePatchList(file, content, "patches");
    }

    /// <summary>
    /// Load a required overlay patch list (a bundle's <c>cordis.patch.yml</c> or a
    /// <c>--patch &lt;path&gt;</c> overlay). A missing file throws, because the caller named this
    /// file — its absence is a misconfiguration, not "no overlay".
    /// </summary>
    public static List<object?> LoadOverlayPatches(string file)
    {
        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"hsh: failed to read overlay {file}: {error.Message}");
        }
        return ParsePatchList(file, content, "overlay");
    }

    private static List<object?> ParsePatchList(string file, string content, string label)
    {
        object? parsed;
        try
        {
            parsed = YamlSubset.Parse(content);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"hsh: failed to parse {label} {file}: {error.Message}");
        }
        if (parsed is not List<object?> list)
        {
            throw new InvalidOperationException($"hsh: {label} {file} must be a top-level YAML array of loader patch entries");
        }
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] is not Dictionary<string, object?>)
            {
                throw new InvalidOperationException(
                    $"hsh: {label} entry {index + 1} in {file} must be a mapping (a loader patch entry)");
            }
        }
        return list;
    }

    /// <summary>Parse an entry-list config file (the empty profile root).</summary>
    public static List<object?> ParseEntryList(string file)
    {
        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"hsh: failed to read config {file}: {error.Message}");
        }
        object? parsed;
        try
        {
            parsed = YamlSubset.Parse(content);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"hsh: failed to parse config {file}: {error.Message}");
        }
        if (parsed is not List<object?> list)
        {
            throw new InvalidOperationException($"hsh: config {file} must be a top-level YAML array of entries");
        }
        return list;
    }

    /// <summary>
    /// Compose patch layers into the effective entry list over an empty root — the same single
    /// <c>applyEntryPatches</c> call the boot include makes, so flag derivation and config dumps
    /// see exactly what mounts.
    /// </summary>
    public static List<EntryOptions> ComposeEntries(IReadOnlyList<List<object?>> layers, Action<string>? warn = null)
    {
        var sink = warn ?? (_ => { });
        var applied = EntryPatches.Apply(new List<object?>(), Flatten(layers), sink);
        return applied.Select(row => row is Dictionary<string, object?> map
            ? EntryPatches.ToEntryOptions(map, sink)
            : throw new InvalidOperationException("config row must be a map")).ToList();
    }

    private static List<object?> Flatten(IReadOnlyList<List<object?>> layers)
        => layers.SelectMany(layer => layer).Select(EntryPatches.Clone).ToList();

    /// <summary>
    /// Resolve the telemetry opt-out switch into its boot patch. ANY non-empty value (including
    /// <c>'0'</c>/<c>'false'</c>) disables. A composition without the telemetry row exports
    /// nothing, so the switch is then trivially satisfied and no patch is generated.
    /// </summary>
    public static EntryPatchOptions? ResolveTelemetryPatch(string? disabledEnv, bool hasRow)
    {
        if (string.IsNullOrEmpty(disabledEnv) || !hasRow) return null;
        return new EntryPatchOptions { Id = TelemetryRowId, Disabled = true };
    }

    /// <summary>
    /// Load <paramref name="name"/> and compose its effective patch stack: bundle layers in
    /// <c>hsh.profile.bundles</c> order, the profile's user layer, the home-level user layer
    /// (<c>$HSH_HOME/cordis.patch.yml</c> — machine-local preferences that apply to every
    /// profile, so it outranks the per-profile layer), then <c>--patch</c> overlays, then the
    /// telemetry switch.
    /// </summary>
    public static ComposedProfile ComposeProfile(string name, IReadOnlyList<string> patchFiles)
    {
        var profile = PrepareProfile(name);
        var homePatches = LoadOptionalPatches(HomePatchPath()) ?? new List<object?>();
        var overlays = patchFiles.SelectMany(file => LoadOverlayPatches(Path.GetFullPath(file))).ToList();
        var bundlePatches = profile.Layers.SelectMany(layer => layer.Patches).ToList();
        var rows = new Dictionary<string, EntryOptions>(StringComparer.Ordinal);
        foreach (var row in ComposeEntries(new List<List<object?>> { bundlePatches, profile.Patches, homePatches, overlays }))
        {
            if (row.Id.Length > 0) rows[row.Id] = row;
        }
        var composedOverlays = overlays.ToList();
        var telemetryPatch = ResolveTelemetryPatch(Environment.GetEnvironmentVariable(TelemetryDisabledEnv), rows.ContainsKey(TelemetryRowId));
        if (telemetryPatch is not null) composedOverlays.Add(telemetryPatch);
        return new ComposedProfile(profile, bundlePatches, homePatches, composedOverlays);
    }

    /// <summary>
    /// Boot one profile invocation end to end: create the context and loader, register the spine
    /// rows, provide the launcher facts (<c>cmdlineArgs</c>, <c>appExit</c>, <c>appReady</c>), apply
    /// the composed patches over the profile's empty root through the Include port's patch
    /// algorithm, and settle the tree. Unknown row names fail loud with the row id (the TS
    /// resolver-manifest contract).
    /// </summary>
    /// <param name="invocation">the profile to boot and its overlays and inner arguments.</param>
    /// <param name="onExit">receives the app's requested exit code (the one-shot runners).</param>
    /// <returns>the settled root context; the app (or the caller) owns disposal.</returns>
    public static async Task<Harness.Cordis.Core.Context> RunProfileAsync(HshInvocation.ProfileInvocation invocation, Action<int>? onExit = null)
    {
        var composed = ComposeProfile(invocation.Profile, invocation.Patches);
        var ctx = new Harness.Cordis.Core.Context();
        var loader = new Harness.Cordis.Plugin.Loader.Loader(ctx, new LoaderConfig { BaseUrl = composed.Profile.Dir });
        SpineRegistry.RegisterAll(loader.Catalog);
        var ready = new AppReady();
        var exit = new AppExit(code =>
        {
            onExit?.Invoke(code);
            ctx.Dispose();
        });
        ctx.Set("hshProfileDir", composed.Profile.Dir);
        ctx.Set("cmdlineArgs", new CmdlineArgs(invocation.Args));
        ctx.Set("appExit", exit);
        ctx.Set("appReady", ready);

        var rootPath = Path.Combine(composed.Profile.Dir, ProfileRootFilename);
        var baseList = ParseEntryList(rootPath);
        var applied = EntryPatches.Apply(baseList, composed.AllPatches().Select(EntryPatches.Clone).ToList(),
            message => loader.Ctx.Logger.Logger("loader").Warn(message));
        var rows = applied.Select(row => row is Dictionary<string, object?> map
            ? EntryPatches.ToEntryOptions(map, message => loader.Ctx.Logger.Logger("loader").Warn(message))
            : throw new InvalidOperationException("config row must be a map")).ToList();
        AuditRows(rows, loader);
        await loader.Root.UpdateAsync(rows);
        await loader.AwaitAsync();
        AuditEntries(loader);
        ready.Commit();
        return ctx;
    }

    /// <summary>Reject rows the spine does not know, naming the row id (fail loud at the earliest resolvable point).</summary>
    private static void AuditRows(IReadOnlyList<EntryOptions> rows, Harness.Cordis.Plugin.Loader.Loader loader)
    {
        foreach (var row in rows)
        {
            if (row.Disabled == true) continue;
            if (row.Name.StartsWith("cordis:", StringComparison.Ordinal)) continue;
            if (loader.Catalog.Resolve(row.Name) is null)
            {
                throw new InvalidOperationException(
                    $"hsh: profile row \"{row.Id}\" names plugin \"{row.Name}\", which the spine does not know "
                    + "(the resolver manifest owns the row-name to service map)");
            }
        }
    }

    /// <summary>Reject a settled tree whose enabled entries never activated.</summary>
    private static void AuditEntries(Harness.Cordis.Plugin.Loader.Loader loader)
    {
        var failed = loader.Entries().Where(entry => entry.Fiber is null && !entry.Disabled).ToList();
        if (failed.Count > 0)
        {
            throw new InvalidOperationException("hsh: plugin(s) failed to load: "
                + string.Join(", ", failed.Select(entry => entry.Options.Name)));
        }
    }
}
