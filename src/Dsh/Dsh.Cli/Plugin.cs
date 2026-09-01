namespace Harness.Cli;

/// <summary>
/// <c>dsh plugin --profile &lt;name&gt; &lt;args...&gt;</c> — profile plugin management: manifest
/// editing of the <c>dsh.profile.bundles</c> list in <c>profile.json</c> (<c>add
/// &lt;bundle&gt;</c>, <c>remove &lt;bundle&gt;</c>, <c>list</c>), plus the NuGet install path
/// (<c>add &lt;packageId&gt; --source &lt;feed&gt;</c>) that resolves a bundle package from a
/// flat-container feed and extracts it under the profile's <c>bundles/</c> directory. The
/// profile is initialized on first use.
/// </summary>
public static class Plugin
{
    /// <summary>
    /// Run one <c>dsh plugin</c> invocation: init if needed, then manage the bundle list.
    /// </summary>
    /// <param name="profile">the profile name.</param>
    /// <param name="args">the verb and its arguments, verbatim from argv.</param>
    /// <returns>the exit code.</returns>
    public static int RunPlugin(string profile, IReadOnlyList<string> args)
    {
        var dir = ProfileBoot.ResolveProfileDir(profile);
        if (!File.Exists(Path.Combine(dir, ProfileBoot.ProfileManifestFilename)))
        {
            var template = ProfileBoot.ProfileTemplates.TryGetValue(profile, out var known) ? known : null;
            ProfileBoot.InitProfile(dir, template?.Bundles ?? ProfileBoot.DefaultProfileBundles, template?.PatchReload);
            Console.Error.WriteLine($"dsh: initialized profile {profile} at {dir}");
        }

        var verb = args[0];
        switch (verb)
        {
            case "add":
                return AddBundleAsync(dir, profile, args).GetAwaiter().GetResult();
            case "remove":
                return RemoveBundle(dir, profile, args);
            case "list":
                return ListBundles(dir, profile);
            default:
                Console.Error.WriteLine(
                    $"dsh: unknown plugin verb \"{verb}\": the .NET port manages profile bundles directly "
                    + "(add <bundle>, remove <bundle>, list)");
                return 1;
        }
    }

    private static async Task<int> AddBundleAsync(string dir, string profile, IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            Console.Error.WriteLine("dsh: plugin add needs a bundle name");
            return 1;
        }
        var bundle = args[1];
        // The NuGet install path: dsh plugin add <packageId> --source <feed> resolves the
        // package from the flat-container feed and extracts its bundle under the profile.
        var sourceIndex = -1;
        for (var i = 0; i < args.Count; i++) { if (args[i] == "--source") { sourceIndex = i; break; } }
        if (sourceIndex >= 0)
        {
            if (sourceIndex + 1 >= args.Count)
            {
                Console.Error.WriteLine("dsh: plugin add --source needs a feed (a flat-container URL or local directory)");
                return 1;
            }
            try
            {
                await NuGetBundleClient.InstallAsync(bundle, args[sourceIndex + 1], dir).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"dsh: failed to install bundle \"{bundle}\": {error.Message}");
                return 1;
            }
        }
        var manifest = ProfileBoot.ReadProfileManifest(dir);
        var bundles = EnsureBundles(manifest);
        if (!bundles.Contains(bundle, StringComparer.Ordinal))
        {
            bundles.Add(bundle);
            ProfileBoot.WriteProfileManifest(dir, manifest);
        }
        Console.Out.WriteLine($"dsh: added {bundle} to profile {profile}");
        return 0;
    }

    private static int RemoveBundle(string dir, string profile, IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            Console.Error.WriteLine("dsh: plugin remove needs a bundle name");
            return 1;
        }
        var bundle = args[1];
        var manifest = ProfileBoot.ReadProfileManifest(dir);
        var bundles = EnsureBundles(manifest);
        if (bundles.Remove(bundle))
        {
            ProfileBoot.WriteProfileManifest(dir, manifest);
        }
        Console.Out.WriteLine($"dsh: removed {bundle} from profile {profile}");
        return 0;
    }

    private static int ListBundles(string dir, string profile)
    {
        var manifest = ProfileBoot.ReadProfileManifest(dir);
        var bundles = manifest.Dsh?.Profile?.Bundles ?? new List<string>();
        foreach (var bundle in bundles)
        {
            Console.Out.WriteLine(bundle);
        }
        return 0;
    }

    private static List<string> EnsureBundles(ProfileManifest manifest)
    {
        manifest.Dsh ??= new DshManifestSection();
        manifest.Dsh.Profile ??= new DshProfileManifest();
        manifest.Dsh.Profile.Bundles ??= new List<string>();
        return manifest.Dsh.Profile.Bundles;
    }
}
