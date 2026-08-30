namespace Dsh.Cli.Tests;

/// <summary>Profile boot, plugin management, config dumps, and the headless one-shot.</summary>
public static class BootTests
{
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
        var code = Dsh.Cli.Program.Main(new[] { "--profile", "headless", "Record your plan for the .NET port as todos." });
        Assert.Equal(0, code, "the headless run exits 0");
        Assert.True(console.Out.ToString().Contains("Todo list recorded."), "the final assistant text prints to stdout");
        Assert.True(Directory.Exists(Path.Combine(home.Dir, "profiles", "headless", "sessions")), "the headless run persists its session under the profile");
    }

    public static void Headless_WithoutATask_ExitsOne()
    {
        using var home = new TempDshHome();
        using var console = new ConsoleCapture();
        var code = Dsh.Cli.Program.Main(new[] { "--profile", "headless" });
        Assert.Equal(1, code, "a task-less headless run exits 1");
        Assert.True(console.Error.ToString().Contains("headless needs a task argument"), "the exact error string");
    }
}
