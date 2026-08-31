using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis.Core;
using Cordis.Cosmokit;
using Cordis.Plugin.Include;
using Cordis.Plugin.Loader;

namespace Dsh.Cli;

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

/// <summary>The bundle half of the <c>dsh</c> manifest section: what a bundle exports.</summary>
public sealed class DshBundleManifest
{
    /// <summary>The patch layer this bundle exports, relative to its package root.</summary>
    public string? Patch { get; set; }
}

/// <summary>The profile half of the <c>dsh</c> manifest section: what a profile directory composes.</summary>
public sealed class DshProfileManifest
{
    /// <summary>Ordered bundle layer list (bundle names).</summary>
    public List<string>? Bundles { get; set; }

    /// <summary>Whether user patch files reload while this profile remains active ("live" or "startup").</summary>
    public string? PatchReload { get; set; }
}

/// <summary>The profile-launcher slice of the <c>dsh</c>-owned manifest section.</summary>
public sealed class DshManifestSection
{
    /// <summary>Bundle metadata consumed by the profile launcher.</summary>
    public DshBundleManifest? Bundle { get; set; }

    /// <summary>Profile metadata consumed by the profile launcher.</summary>
    public DshProfileManifest? Profile { get; set; }
}

/// <summary>
/// The C#-world profile manifest (deviation from the TS <c>package.json</c> slice
/// <c>dsh.profile</c>): the .NET port keeps one <c>profile.json</c> per profile and per bundle,
/// carrying the same <c>dsh.profile.bundles</c> / <c>dsh.bundle.patch</c> fields. The npm
/// package story (dependencies, pnpm-installed bundles) arrives with NuGet packaging in Phase 8.
/// </summary>
public sealed class ProfileManifest
{
    /// <summary>Profile display name (informational).</summary>
    public string? Name { get; set; }

    /// <summary>The <c>dsh</c> manifest section.</summary>
    public DshManifestSection? Dsh { get; set; }
}

/// <summary>Installation-owned defaults used when a shipped profile is first opened.</summary>
public sealed record ProfileTemplate(IReadOnlyList<string> Bundles, string PatchReload);

/// <summary>The invocation's inner arguments, provided to app plugins as the <c>cmdlineArgs</c> service.</summary>
public sealed record CmdlineArgs(IReadOnlyList<string> Args);

/// <summary>Bounded process-exit request, provided as the <c>appExit</c> service (port of dsh-cmdline's AppExit).</summary>
public sealed record AppExit(Action<int> Exit);

/// <summary>
/// Successful application-startup signal owned by the launcher (port of dsh-cmdline's AppReady):
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
                Console.Error.WriteLine($"dsh: appReady listener threw: {error.Message}");
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
/// Shared profile boot for every <c>dsh</c> surface (port of <c>apps/cli/src/profile-boot.ts</c>
/// and the profile slice of <c>@deepseek-ai/dsh-app-boot</c>): resolve the profile under
/// <c>$DSH_HOME/profiles/&lt;name&gt;</c>, rewrite its empty <c>cordis.yml</c> root, stack its patch
/// layers (bundle layers in <c>dsh.profile.bundles</c> order, the profile's own
/// <c>cordis.patch.yml</c>, the home-level <c>$DSH_HOME/cordis.patch.yml</c>, <c>--patch</c>
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
    public const string DshHomeEnv = "DSH_HOME";

    /// <summary>The session-telemetry row id the DSH_TELEMETRY_DISABLED switch targets.</summary>
    public const string TelemetryRowId = "session-telemetry-otel";

    /// <summary>Environment variable naming the telemetry opt-out switch.</summary>
    public const string TelemetryDisabledEnv = "DSH_TELEMETRY_DISABLED";

    /// <summary>The bundle list a <c>dsh plugin</c> init uses for a name with no shipped template.</summary>
    public static readonly IReadOnlyList<string> DefaultProfileBundles = new[] { "@deepseek-ai/dsh-base" };

    /// <summary>Custom profiles retain the historical live patch-file behavior.</summary>
    public const string DefaultProfilePatchReload = "live";

    /// <summary>The shipped profile templates auto-initialized on first use, by name.</summary>
    public static readonly IReadOnlyDictionary<string, ProfileTemplate> ProfileTemplates =
        new Dictionary<string, ProfileTemplate>(StringComparer.Ordinal)
        {
            ["headless"] = new(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-headless" }, "startup"),
            ["tui"] = new(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-tui" }, "live"),
            ["web"] = new(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web" }, "live"),
            ["sdk"] = new(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-sdk" }, "startup"),
        };

    /// <summary>The empty root entry list every profile tree patches over.</summary>
    public const string ProfileRootConfig = """
        # dsh profile root — an empty entry list. The tree is composed as patches:
        # each bundle in profile.json's dsh.profile.bundles, then cordis.patch.yml, then any
        # --patch overlays. Edit cordis.patch.yml, not this file.
        []
        """;

    private const string ProfilePatchTemplate = """
        # Your patch layer for this dsh profile, applied after every bundle layer:
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

    /// <summary>Resolve the harness home: <c>$DSH_HOME</c>, else the default <c>~/.dsh</c>.</summary>
    public static string ResolveDshHome() => HomePaths.ResolveDshHome();

    /// <summary>Resolve a profile's directory under the Harness home (may not exist yet).</summary>
    public static string ResolveProfileDir(string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name == "." || name == ".."
            || name == "node_modules")
        {
            throw new InvalidOperationException($"dsh: invalid profile name {JsonSerializer.Serialize(name)}");
        }
        return Path.Combine(ResolveDshHome(), ProfilesDir, name);
    }

    /// <summary>The home-level user patch layer (<c>$DSH_HOME/cordis.patch.yml</c>), applied over every profile's own layer.</summary>
    public static string HomePatchPath() => Path.Combine(ResolveDshHome(), ProfilePatchFilename);

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
                Name = "dsh-profile-" + Path.GetFileName(dir),
                Dsh = new DshManifestSection
                {
                    Profile = new DshProfileManifest
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
            throw new InvalidOperationException($"dsh: failed to read profile manifest {path}: {error.Message}");
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<ProfileManifest>(raw, ManifestJson);
            if (parsed is null)
            {
                throw new InvalidOperationException($"dsh: profile manifest {path} must hold a JSON object");
            }
            return parsed;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"dsh: profile manifest {path} must hold a JSON object: {error.Message}");
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
            $"dsh: cannot resolve profile bundle {JsonSerializer.Serialize(bundleName)} from the dsh installation or {profileDir}; "
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
    /// Load a profile: resolve every <c>dsh.profile.bundles</c> entry to its patch layer and parse
    /// the profile's own patch file. A listed bundle without a <c>dsh.bundle</c> manifest fails
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
                    $"dsh: profile {JsonSerializer.Serialize(name)} does not exist; create it with 'dsh plugin --profile {name} add <package>'");
            }
            InitProfile(dir, template.Bundles, template.PatchReload);
        }
        var manifest = ReadProfileManifest(dir);
        var bundles = manifest.Dsh?.Profile?.Bundles ?? new List<string>();
        var rawPatchReload = manifest.Dsh?.Profile?.PatchReload;
        if (rawPatchReload is not null && rawPatchReload is not ("live" or "startup"))
        {
            throw new InvalidOperationException(
                $"dsh: profile manifest {Path.Combine(dir, ProfileManifestFilename)} dsh.profile.patchReload must be \"live\" or \"startup\"");
        }
        var patchReload = rawPatchReload ?? DefaultProfilePatchReload;
        var layers = bundles.Select(packageName =>
        {
            var packageDir = ResolveBundleDir(packageName, dir);
            var bundleManifest = ReadProfileManifest(packageDir);
            var declared = bundleManifest.Dsh?.Bundle?.Patch;
            if (declared is null)
            {
                throw new InvalidOperationException(
                    $"dsh: profile bundle {JsonSerializer.Serialize(packageName)} declares no dsh.bundle in its profile.json");
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
            throw new InvalidOperationException($"dsh: failed to read patches {file}: {error.Message}");
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
            throw new InvalidOperationException($"dsh: failed to read overlay {file}: {error.Message}");
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
            throw new InvalidOperationException($"dsh: failed to parse {label} {file}: {error.Message}");
        }
        if (parsed is not List<object?> list)
        {
            throw new InvalidOperationException($"dsh: {label} {file} must be a top-level YAML array of loader patch entries");
        }
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] is not Dictionary<string, object?>)
            {
                throw new InvalidOperationException(
                    $"dsh: {label} entry {index + 1} in {file} must be a mapping (a loader patch entry)");
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
            throw new InvalidOperationException($"dsh: failed to read config {file}: {error.Message}");
        }
        object? parsed;
        try
        {
            parsed = YamlSubset.Parse(content);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"dsh: failed to parse config {file}: {error.Message}");
        }
        if (parsed is not List<object?> list)
        {
            throw new InvalidOperationException($"dsh: config {file} must be a top-level YAML array of entries");
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
    /// <c>dsh.profile.bundles</c> order, the profile's user layer, the home-level user layer
    /// (<c>$DSH_HOME/cordis.patch.yml</c> — machine-local preferences that apply to every
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
    public static async Task<Cordis.Core.Context> RunProfileAsync(DshInvocation.ProfileInvocation invocation, Action<int>? onExit = null)
    {
        var composed = ComposeProfile(invocation.Profile, invocation.Patches);
        var ctx = new Cordis.Core.Context();
        var loader = new Cordis.Plugin.Loader.Loader(ctx, new LoaderConfig { BaseUrl = composed.Profile.Dir });
        SpineRegistry.RegisterAll(loader.Catalog);
        var ready = new AppReady();
        var exit = new AppExit(code =>
        {
            onExit?.Invoke(code);
            ctx.Dispose();
        });
        ctx.Set("dshProfileDir", composed.Profile.Dir);
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
    private static void AuditRows(IReadOnlyList<EntryOptions> rows, Cordis.Plugin.Loader.Loader loader)
    {
        foreach (var row in rows)
        {
            if (row.Disabled == true) continue;
            if (row.Name.StartsWith("cordis:", StringComparison.Ordinal)) continue;
            if (loader.Catalog.Resolve(row.Name) is null)
            {
                throw new InvalidOperationException(
                    $"dsh: profile row \"{row.Id}\" names plugin \"{row.Name}\", which the spine does not know "
                    + "(the resolver manifest owns the row-name to service map)");
            }
        }
    }

    /// <summary>Reject a settled tree whose enabled entries never activated.</summary>
    private static void AuditEntries(Cordis.Plugin.Loader.Loader loader)
    {
        var failed = loader.Entries().Where(entry => entry.Fiber is null && !entry.Disabled).ToList();
        if (failed.Count > 0)
        {
            throw new InvalidOperationException("dsh: plugin(s) failed to load: "
                + string.Join(", ", failed.Select(entry => entry.Options.Name)));
        }
    }
}
