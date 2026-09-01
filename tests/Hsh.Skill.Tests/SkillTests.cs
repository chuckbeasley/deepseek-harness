using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Tools;

namespace Harness.Skill.Tests;

/// <summary>
/// Skill registry, filesystem provider, and catalog-tool tests (ported from
/// packages/skill/{skill,skill-filesystem,tool-skill}).
/// </summary>
public static class SkillTests
{
    public static async Task DiscoveryListsDirectoryAndFlatSkillsFromARoot()
    {
        using var root = TempRoot.Create();
        WriteSkill(root.Path, "alpha/SKILL.md", Frontmatter(
            "Do the alpha thing.",
            ("name", "alpha"),
            ("description", "Alpha skill"),
            ("whenToUse", "alpha tasks")));
        WriteSkill(root.Path, "beta.md", Frontmatter(
            "Do the beta thing.",
            ("name", "beta"),
            ("description", "Beta skill")));
        // A markdown file without frontmatter is not a skill and must be skipped.
        WriteSkill(root.Path, "notes.md", "Plain notes, no frontmatter.\n");

        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            registry.RegisterProvider(new FileSystemSkillProvider(root.Path));
            var skills = await registry.ListAsync();
            Assert.Equal(new[] { "alpha", "beta" }, skills.Select(s => s.Name).ToArray());
            var alpha = skills.Single(s => s.Name == "alpha");
            Assert.Equal("Alpha skill", alpha.Description);
            Assert.Equal("alpha tasks", alpha.WhenToUse);
            Assert.Equal("custom", alpha.Source);
            Assert.Equal("filesystem", alpha.Provider);
            Assert.True(alpha.Invocation.ModelInvocable);
            Assert.True(alpha.Invocation.UserInvocable);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task LoadsOneSkillMetadataAndInstructions()
    {
        using var root = TempRoot.Create();
        WriteSkill(root.Path, "alpha/SKILL.md", Frontmatter(
            "Do the alpha thing.",
            ("name", "alpha"),
            ("description", "Alpha skill"),
            ("whenToUse", "alpha tasks")));

        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            registry.RegisterProvider(new FileSystemSkillProvider(root.Path));
            var skill = await registry.GetAsync("alpha");
            Assert.NotNull(skill);
            Assert.Equal("Do the alpha thing.", skill!.Content);
            Assert.Equal("alpha tasks", skill.WhenToUse);
            Assert.Equal(Path.Combine(root.Path, "alpha", "SKILL.md"), skill.Path);
            var resourceBase = skill.ResourceBase as SkillResourceDirectory;
            Assert.NotNull(resourceBase, "resource base must be a directory");
            Assert.Equal(Path.Combine(root.Path, "alpha"), resourceBase!.Path);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task MissingSkillAndInvalidNamesReturnNull()
    {
        using var root = TempRoot.Create();
        WriteSkill(root.Path, "alpha/SKILL.md", Frontmatter(
            "Do the alpha thing.",
            ("name", "alpha"),
            ("description", "Alpha skill")));

        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            registry.RegisterProvider(new FileSystemSkillProvider(root.Path));
            Assert.Null(await registry.GetAsync("unknown"), "an unknown skill name must resolve to null");
            Assert.Null(await registry.GetAsync("Not-A-Valid-Name"), "a name outside the skill grammar must resolve to null");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task MissingSkillRootFailsLoud()
    {
        using var root = TempRoot.Create();
        var missing = Path.Combine(root.Path, "does-not-exist");

        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            registry.RegisterProvider(new FileSystemSkillProvider(missing));
            var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => registry.ListAsync());
            Assert.True(error.Message.Contains("does not exist", StringComparison.Ordinal), $"unexpected message: {error.Message}");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task CatalogToolExecutesThroughToolRuntime()
    {
        using var root = TempRoot.Create();
        WriteSkill(root.Path, "alpha/SKILL.md", Frontmatter(
            "Do the alpha thing.",
            ("name", "alpha"),
            ("description", "Alpha skill")));
        WriteSkill(root.Path, "private.md", Frontmatter(
            "Do the private thing.",
            ("name", "private-skill"),
            ("description", "Private skill"),
            ("disable-model-invocation", "true")));

        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            registry.RegisterProvider(new FileSystemSkillProvider(root.Path));
            var tools = new ToolRuntime(ctx);
            SkillTools.Register(ctx);

            var success = await tools.ExecuteAsync(Call("c1", "skill", new Dictionary<string, object?> { ["name"] = "alpha" }), CancellationToken.None);
            Assert.True(!success.IsError, $"expected success but got {DescribeResult(success)}");
            Assert.True(success is ToolExecutionSuccess, "expected a successful tool result");
            var value = (ToolExecutionSuccess)success;
            Assert.Equal("alpha", value!.Value.GetProperty("name").GetString());
            Assert.Equal("filesystem", value.Value.GetProperty("provider").GetString());
            Assert.Equal("Do the alpha thing.", value.Value.GetProperty("content").GetString());
            var rendered = Assert.Single(value.Content);
            Assert.True(rendered is TextBlock, "the rendered content must be a text block");
            var text = (TextBlock)rendered;
            Assert.True(text!.Text.StartsWith("<skill_content name=\"alpha\">", StringComparison.Ordinal), text.Text);
            Assert.True(text.Text.Contains("Do the alpha thing.", StringComparison.Ordinal), text.Text);

            var invalidName = await tools.ExecuteAsync(Call("c2", "skill", new Dictionary<string, object?> { ["name"] = "Not-A-Skill" }), CancellationToken.None);
            Assert.True(invalidName.IsError, "an invalid skill name must fail the call");

            var unknown = await tools.ExecuteAsync(Call("c3", "skill", new Dictionary<string, object?> { ["name"] = "unknown" }), CancellationToken.None);
            Assert.True(unknown.IsError, "an unknown skill must fail the call");

            var privateCall = await tools.ExecuteAsync(Call("c4", "skill", new Dictionary<string, object?> { ["name"] = "private-skill" }), CancellationToken.None);
            Assert.True(privateCall.IsError, "a model-disabled skill must fail the call");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task RuntimeRegistrationAndRegistryRules()
    {
        using var root = TempRoot.Create();
        WriteSkill(root.Path, "alpha/SKILL.md", Frontmatter(
            "Do the alpha thing.",
            ("name", "alpha"),
            ("description", "Alpha skill")));

        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            registry.Register(new SkillRegistration("injected", "Injected skill", "custom", "Do the injected thing."));
            registry.RegisterProvider(new FileSystemSkillProvider(root.Path));

            var skills = await registry.ListAsync();
            Assert.Equal(new[] { "alpha", "injected" }, skills.Select(s => s.Name).ToArray());
            var injected = skills.Single(s => s.Name == "injected");
            Assert.Equal("runtime", injected.Provider);
            var loaded = await registry.GetAsync("injected");
            Assert.NotNull(loaded);
            Assert.Equal("Do the injected thing.", loaded!.Content);

            Assert.Throws<InvalidOperationException>(
                () => registry.RegisterProvider(new FileSystemSkillProvider(root.Path, providerName: "filesystem")),
                "a duplicate provider name must throw");
            Assert.Throws<ArgumentException>(
                () => registry.RegisterProvider(new FileSystemSkillProvider(root.Path, providerName: "runtime")),
                "the runtime provider name is reserved");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task RenderSkillContentIsPinnedAndEscapes()
    {
        var expected =
            "<skill_content name=\"alpha\">\n" +
            "<skill_resources>\n" +
            "Base directory for this skill: /tmp/root/alpha\n" +
            "Resolve relative paths mentioned by this skill against the base directory before using them. Load referenced resources only as needed.\n" +
            "</skill_resources>\n" +
            "\n" +
            "<skill_instructions>\n" +
            "Do the alpha thing.\n" +
            "</skill_instructions>\n" +
            "</skill_content>";
        var rendered = SkillTools.RenderSkillContent(new SkillContent(
            "alpha",
            "filesystem",
            "Do the alpha thing.",
            new SkillResourceDirectory("/tmp/root/alpha")));
        Assert.Equal(expected, rendered);

        var escaped = SkillTools.EscapeText("a < b > c & d");
        Assert.Equal("a &lt; b &gt; c &amp; d", escaped);

        var injected = SkillTools.RenderSkillContent(new SkillContent("a\"<b>", "p", "body", null));
        Assert.True(injected.StartsWith("<skill_content name=\"a&quot;&lt;b>\">", StringComparison.Ordinal), injected);
        Assert.True(injected.Contains("</skill_instructions>", StringComparison.Ordinal), injected);
    }

    public static async Task SkillToolRegistrationFailsLoudWithoutToolRegistry()
    {
        var ctx = new Context();
        try
        {
            var registry = new SkillRegistry(ctx);
            Assert.Throws<InvalidOperationException>(() => SkillTools.Register(ctx), "the missing tool registry must fail loud");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    private static string DescribeResult(ToolExecutionResult result) => result.IsError ? "error" : "success";

    private static ToolExecutionInput Call(string id, string name, Dictionary<string, object?> args)
        => new(new ToolCallId(id), name, JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private static string Frontmatter(string body, params (string Key, string Value)[] fields)
    {
        var lines = new List<string> { "---" };
        lines.AddRange(fields.Select(field => $"{field.Key}: {field.Value}"));
        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add(body);
        return string.Join("\n", lines);
    }

    private static string WriteSkill(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
