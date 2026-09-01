namespace Harness.Cli.Tests;

/// <summary>Profile boot, plugin management, config dumps, and the headless one-shot.</summary>
public static class BootTests
{
    public static void Plugin_InstallsABundleFromANuGetFeed()
    {
        using var home = new TempDshHome();
        using var console = new ConsoleCapture();
        var feed = Path.Combine(Path.GetTempPath(), "dsh-cli-feed-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteBundlePackage(feed, "harness.testbundle", "0.1.0", bundleContent: true);
            Assert.Equal(0, Plugin.RunPlugin("headless", new[] { "add", "harness.testbundle", "--source", feed }), "the NuGet add exits 0");
            Assert.True(console.Out.ToString().Contains("dsh: added harness.testbundle to profile headless"), "the add reports the profile");

            var profileDir = Path.Combine(home.Dir, "profiles", "headless");
            var manifest = ProfileBoot.ReadProfileManifest(profileDir);
            Assert.True(manifest.Dsh?.Profile?.Bundles?.Contains("harness.testbundle") == true, "the manifest records the installed bundle");
            var bundleDir = ProfileBoot.ResolveBundleDir("harness.testbundle", profileDir);
            Assert.True(File.Exists(Path.Combine(bundleDir, "profile.json")), "the bundle manifest is extracted");
            Assert.True(File.Exists(Path.Combine(bundleDir, "cordis.patch.yml")), "the bundle patch is extracted");
            Assert.True(File.ReadAllText(Path.Combine(bundleDir, "cordis.patch.yml")).Contains("plugin-nuget-test"), "the extracted patch carries the bundle row");

            // A package without the bundle pair is refused.
            WriteBundlePackage(feed, "harness.notabundle", "0.1.0", bundleContent: false);
            console.Out.GetStringBuilder().Clear();
            Assert.Equal(1, Plugin.RunPlugin("headless", new[] { "add", "harness.notabundle", "--source", feed }), "a non-bundle package fails");
            Assert.True(console.Error.ToString().Contains("not a Harness bundle"), "the refusal names the missing bundle pair");
        }
        finally
        {
            Directory.Delete(feed, recursive: true);
        }
    }

    /// <summary>Write one bundle nupkg into the hierarchical flat-container feed layout.</summary>
    private static void WriteBundlePackage(string feed, string id, string version, bool bundleContent)
    {
        var dir = Path.Combine(feed, id, version);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.{version}.nupkg");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            var patch = zip.CreateEntry("cordis.patch.yml");
            using (var writer = new StreamWriter(patch.Open()))
            {
                writer.Write("- insert:\n    - id: plugin-nuget-test\n      name: plugin-nuget-test\n");
            }
            if (bundleContent)
            {
                var manifest = zip.CreateEntry("profile.json");
                using (var writer = new StreamWriter(manifest.Open()))
                {
                    writer.Write("{\n  \"name\": \"harness.testbundle\",\n  \"dsh\": { \"bundle\": { \"patch\": \"cordis.patch.yml\" } }\n}\n");
                }
            }
        }
    }

    public static void Plugin_InitializesAndManagesBundles()
    {
        using var home = new TempDshHome();
        using var console = new ConsoleCapture();

        // First use initializes the headless profile from its template and lists its bundles.
        Assert.Equal(0, Plugin.RunPlugin("headless", new[] { "list" }), "list exits 0");
        var listed = console.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Sequence(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-headless" }, listed, "the template bundles list");
        Assert.True(File.Exists(Path.Combine(home.Dir, "profiles", "headless", "profile.json")), "the manifest is initialized");
        Assert.True(File.Exists(Path.Combine(home.Dir, "profiles", "headless", "cordis.patch.yml")), "the user patch layer is initialized");

        // add / list / remove round-trip the bundle list.
        console.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Plugin.RunPlugin("headless", new[] { "add", "my-bundle" }), "add exits 0");
        console.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Plugin.RunPlugin("headless", new[] { "list" }), "list exits 0 after add");
        listed = console.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Sequence(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-headless", "my-bundle" }, listed, "the added bundle joins the list");

        console.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Plugin.RunPlugin("headless", new[] { "remove", "my-bundle" }), "remove exits 0");
        console.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Plugin.RunPlugin("headless", new[] { "list" }), "list exits 0 after remove");
        listed = console.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Sequence(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-headless" }, listed, "the removed bundle leaves the list");
    }

    public static void DumpConfig_ComposesLayersBootFree()
    {
        using var home = new TempDshHome();
        var profileDir = Path.Combine(home.Dir, "profiles", "custom");
        ProfileBoot.InitProfile(profileDir, new[] { "custom-bundle" }, "startup");
        var bundleDir = Path.Combine(profileDir, "bundles", "custom-bundle");
        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "profile.json"),
            "{\n  \"name\": \"custom-bundle\",\n  \"dsh\": { \"bundle\": { \"patch\": \"cordis.patch.yml\" } }\n}\n");
        File.WriteAllText(Path.Combine(bundleDir, "cordis.patch.yml"), "- insert:\n    - id: sessions\n      name: sessions\n");
        File.WriteAllText(Path.Combine(profileDir, "cordis.patch.yml"),
            "- id: sessions\n  name: sessions\n  config:\n    seed: user-layer\n");

        using var console = new ConsoleCapture();
        DumpConfig.RunDumpConfig("custom", defaultOnly: false, Array.Empty<string>());
        var output = console.Out.ToString();
        Assert.True(output.Contains("# == custom-bundle"), "the dump names the bundle layer");
        Assert.True(output.Contains("sessions"), "the dump renders the composed rows");
        Assert.True(output.Contains(Path.Combine(profileDir, "cordis.patch.yml")), "the dump names the profile's own patch layer");

        console.Out.GetStringBuilder().Clear();
        DumpConfig.RunDumpConfig("custom", defaultOnly: true, Array.Empty<string>());
        Assert.False(console.Out.ToString().Contains("cordis.patch.yml"), "the default dump omits the user layer");
    }

    public static void Headless_RunsOneTaskThroughTheRealLoop()
    {
        using var home = new TempDshHome();
        using var console = new ConsoleCapture();
        var code = Harness.Cli.Program.Main(new[] { "--profile", "headless", "Record your plan for the .NET port as todos." });
        Assert.Equal(0, code, "the headless run exits 0");
        Assert.True(console.Out.ToString().Contains("Todo list recorded."), "the final assistant text prints to stdout");
        Assert.True(Directory.Exists(Path.Combine(home.Dir, "profiles", "headless", "sessions")), "the headless run persists its session under the profile");
    }

    public static void Headless_WithoutATask_ExitsOne()
    {
        using var home = new TempDshHome();
        using var console = new ConsoleCapture();
        var code = Harness.Cli.Program.Main(new[] { "--profile", "headless" });
        Assert.Equal(1, code, "a task-less headless run exits 1");
        Assert.True(console.Error.ToString().Contains("headless needs a task argument"), "the exact error string");
    }
}
