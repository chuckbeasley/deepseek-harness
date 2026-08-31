using Cordis.Plugin.Loader;

namespace Dsh.Preset.Tests;

/// <summary>Temp-directory fixture helpers for preset discovery tests.</summary>
internal static class PresetFixture
{
    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-preset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void Remove(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

/// <summary>Discovery and resolution coverage for the preset capability seam.</summary>
public static class PresetTests
{
    public static void DiscoveryListsPresetsFromATempRoot()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            PresetFixture.Write(Path.Combine(root, "standard", "agent.cordis.yml"), "- name: '@deepseek-ai/dsh-tool-fs'\n");
            PresetFixture.Write(Path.Combine(root, "minimal", "agent.cordis.yml"), "- name: '@deepseek-ai/dsh-persona'\n");
            // Not preset slots: a non-matching directory name, a plain file, and a dot-directory.
            PresetFixture.Write(Path.Combine(root, "Invalid_Name", "agent.cordis.yml"), "- name: 'x'\n");
            PresetFixture.Write(Path.Combine(root, "notes.txt"), "not a preset\n");
            PresetFixture.Write(Path.Combine(root, ".hidden", "agent.cordis.yml"), "- name: 'x'\n");

            var provider = new FilePresetProvider(root);
            var found = provider.Discover();

            Assert.Equal(2, found.Count, "only id-valid preset directories must be discovered");
            Assert.Sequence(new[] { "minimal", "standard" }, found.Select(p => p.Id).ToArray(), "presets must be ordered by id");
            Assert.True(found.All(p => p.Broken is null), "healthy compositions must not be reported broken");
            Assert.True(found.All(p => p.CompositionPath.EndsWith("agent.cordis.yml", StringComparison.Ordinal)), "the composition path must point at the composition file");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void MissingCompositionIsReportedBroken()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "broken"));

            var found = new FilePresetProvider(root).Discover();

            var row = Assert.SinglePreset(found, "broken");
            Assert.NotNull(row.Broken, "a directory without a composition must be reported broken");
            Assert.Contains("agent.cordis.yml is missing", row.Broken!, "the broken reason must name the missing composition");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void MalformedCompositionIsReportedBroken()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            PresetFixture.Write(Path.Combine(root, "malformed", "agent.cordis.yml"), "this is not yaml\n");

            var row = Assert.SinglePreset(new FilePresetProvider(root).Discover(), "malformed");

            Assert.NotNull(row.Broken, "an unparsable composition must be reported broken");
            Assert.Contains("not valid YAML", row.Broken!, "the broken reason must call out the YAML parse failure");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void WrongShapedCompositionIsReportedBroken()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            // A map where an entry list is required.
            PresetFixture.Write(Path.Combine(root, "wrong", "agent.cordis.yml"), "name: not-a-list\n");
            // A row with no plugin name.
            PresetFixture.Write(Path.Combine(root, "noname", "agent.cordis.yml"), "- config: {}\n");

            var found = new FilePresetProvider(root).Discover();

            var wrong = Assert.SinglePreset(found, "wrong");
            Assert.Contains("the composition must be a top-level list of plugin rows", wrong.Broken!, "a map document must fail the entry-list shape check");
            var noname = Assert.SinglePreset(found, "noname");
            Assert.Contains("row 1 names no plugin", noname.Broken!, "a row without a name must fail the entry-list shape check");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void ResolveComposesLayersThroughPatches()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            PresetFixture.Write(Path.Combine(root, "standard", "agent.cordis.yml"),
                "- id: persona\n" +
                "  name: '@deepseek-ai/dsh-persona'\n" +
                "  config:\n" +
                "    text: You are the preset identity.\n" +
                "- id: tool-bash\n" +
                "  name: '@deepseek-ai/dsh-tool-bash'\n");
            var patches = new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = "tool-bash", ["disabled"] = true },
                new Dictionary<string, object?>
                {
                    ["id"] = "persona",
                    ["config"] = new Dictionary<string, object?> { ["text"] = "patched persona" },
                },
                new Dictionary<string, object?>
                {
                    ["insert"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["id"] = "tool-extra", ["name"] = "@deepseek-ai/dsh-tool-extra" },
                    },
                },
            };

            var composed = new FilePresetProvider(root, patches).Resolve("standard");

            Assert.Equal("standard", composed.Id, "the resolved preset must carry its id");
            Assert.Equal(3, composed.Rows.Count, "the patches must add one row to the two-file rows");
            Assert.Equal("persona", composed.Rows[0].Id, "the first row must keep its id");
            var personaConfig = (Dictionary<string, object?>)composed.Rows[0].Config!;
            Assert.Equal("patched persona", personaConfig["text"], "the config patch must replace the row config");
            Assert.Equal("tool-bash", composed.Rows[1].Id, "the second row must keep its id");
            Assert.Equal(true, composed.Rows[1].Disabled, "the disabled patch must gate the row");
            Assert.Equal("tool-extra", composed.Rows[2].Id, "the insert patch must append its row at the end");
            Assert.Equal("@deepseek-ai/dsh-tool-extra", composed.Rows[2].Name, "the inserted row must carry its plugin name");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void ResolveComposesGroupRows()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            PresetFixture.Write(Path.Combine(root, "grouped", "agent.cordis.yml"),
                "- id: planning\n" +
                "  name: cordis:group\n" +
                "  group: true\n" +
                "  config:\n" +
                "    - id: plan-mode\n" +
                "      name: '@deepseek-ai/dsh-plan-mode'\n");

            var composed = new FilePresetProvider(root).Resolve("grouped");

            Assert.Equal(1, composed.Rows.Count, "the group row must be the only top-level row");
            Assert.Equal(true, composed.Rows[0].Group, "the group row must stay a group");
            var children = (List<EntryOptions>)composed.Rows[0].Config!;
            Assert.Equal(1, children.Count, "the group must carry its child rows");
            Assert.Equal("plan-mode", children[0].Id, "the child row must keep its id");
            Assert.Equal("@deepseek-ai/dsh-plan-mode", children[0].Name, "the child row must carry its plugin name");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void ResolveUnknownPresetFailsLoud()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new FilePresetProvider(root).Resolve("nope"),
                "an unknown preset id must fail resolution");
            Assert.Contains("\"nope\" not found", error.Message, "the failure must name the missing id");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void ResolveBrokenPresetFailsLoud()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "broken"));

            var error = Assert.Throws<InvalidOperationException>(
                () => new FilePresetProvider(root).Resolve("broken"),
                "a broken preset must fail resolution");
            Assert.Contains("failed to mount", error.Message, "the failure must be a mount-style refusal");
            Assert.Contains("is missing", error.Message, "the failure must carry the discovery reason");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void ResolveEmptyIdFailsLoud()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new FilePresetProvider(root).Resolve(""),
                "an empty preset id must be refused before any domain operation");
            Assert.Contains("must be a non-empty string", error.Message, "the failure must state the empty-id rule");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void AbsentRootYieldsNoPresets()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            var missing = Path.Combine(root, "does-not-exist");
            var found = new FilePresetProvider(missing).Discover();

            Assert.Equal(0, found.Count, "an absent root must yield no presets rather than throwing");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }

    public static void DiscoveryRecordsTheRootTrust()
    {
        var root = PresetFixture.CreateRoot();
        try
        {
            PresetFixture.Write(Path.Combine(root, "writer", "agent.cordis.yml"), "- name: 'x'\n");
            var user = new FilePresetProvider(root);
            Assert.Equal(PresetTrust.User, Assert.SinglePreset(user.Discover(), "writer").Trust,
                "the default root trust is user");
            var system = new FilePresetProvider(root, trust: PresetTrust.System);
            Assert.Equal(PresetTrust.System, Assert.SinglePreset(system.Discover(), "writer").Trust,
                "a system root classifies every preset under it as system");
            Assert.Equal(PresetTrust.System, system.Resolve("writer").Trust,
                "resolve carries the root trust");
        }
        finally
        {
            PresetFixture.Remove(root);
        }
    }
}
