using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Tools;

namespace Harness.SystemPrompt.Tests;

/// <summary>
/// System-prompt registry and assembly tests (ported from packages/core/system-prompt). Every
/// assembly carries the plugin's own built-ins — harness:identity and deployment:persona — so
/// registry-mechanic assertions filter them with <see cref="Contributed"/>.
/// </summary>
public static class SystemPromptTests
{
    private const string Identity = "You are an AI agent powered by DeepSeek Harness.";
    private const string IdentityAndPersona = "You are an AI agent powered by DeepSeek Harness.\n\nYou are DeepSeek Harness.";

    private static (Context Ctx, SystemPromptService SystemPrompt) Boot(SystemPromptConfig? config = null)
    {
        var ctx = new Context();
        return (ctx, new SystemPromptService(ctx, config));
    }

    private static IReadOnlyList<AssembledSection> Contributed(PromptAssembly assembly)
        => assembly.Sections.Where(section => section.Name is not ("harness:identity" or "deployment:persona")).ToArray();

    private static ToolSchema Tool(string name, string? description = null)
        => new(name, description ?? name, JsonSerializer.SerializeToElement(new Dictionary<string, object?>()));

    private static ToolDefinition ToolDefinition(string name, string? description = null) => new(
        name,
        description ?? name,
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>())));

    public static async Task RegistersHarnessIdentityAndPersona()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig { Persona = "You are DeepSeek Harness." });
        try
        {
            var assembly = await systemPrompt.AssembleAsync();
            Assert.Equal(new[] { "harness:identity", "deployment:persona" }, assembly.Sections.Select(s => s.Name).ToArray());
            Assert.Equal(IdentityAndPersona, systemPrompt.RenderPrompt(assembly));
            // The names are reserved by the plugin — one owner per section.
            Assert.Throws<InvalidOperationException>(
                () => systemPrompt.RegisterSection(new PromptSection("deployment:persona", 0, PromptText.Static("imposter"))),
                "a duplicate persona section must throw");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task RendersNoPersonaForAPersonaLessDeployment()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            Assert.Equal(Identity, systemPrompt.RenderPrompt(await systemPrompt.AssembleAsync()));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task CanOmitHarnessIdentity()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig
        {
            IncludeHarnessIdentity = false,
            Persona = "You are a helpful software engineer assistant.",
        });
        try
        {
            var assembly = await systemPrompt.AssembleAsync();
            Assert.Equal(new[] { "deployment:persona" }, assembly.Sections.Select(s => s.Name).ToArray());
            Assert.Equal("You are a helpful software engineer assistant.", systemPrompt.RenderPrompt(assembly));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task AssemblesSectionsInOrder_WithResolvedTextAndCollectedTools()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig { Persona = "You are DeepSeek Harness." });
        try
        {
            systemPrompt.RegisterSection(new PromptSection("cwd", 20, PromptText.Provider(_ => "cwd: /tmp")));
            systemPrompt.RegisterSection(new PromptSection("rules", 10, PromptText.Static("Be precise.")));
            systemPrompt.RegisterToolProvider(_ => new ToolProviderResult(new[] { Tool("echo", "echo back") }));

            var assembly = await systemPrompt.AssembleAsync();
            Assert.Equal(new[] { "harness:identity", "deployment:persona", "rules", "cwd" }, assembly.Sections.Select(s => s.Name).ToArray());
            Assert.Equal(new[] { Identity, "You are DeepSeek Harness.", "Be precise.", "cwd: /tmp" }, assembly.Sections.Select(s => s.Text).ToArray());
            Assert.Equal(new[] { "echo" }, assembly.Tools.Select(t => t.Name).ToArray());
            Assert.Equal("echo back", assembly.Tools[0].Description);
            Assert.Equal(IdentityAndPersona + "\n\nBe precise.\n\ncwd: /tmp", systemPrompt.RenderPrompt(assembly));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task BreaksEqualSectionOrdersByCodeUnitName()
    {
        foreach (var names in new[] { new[] { "äther", "zeta" }, new[] { "zeta", "äther" } })
        {
            var (ctx, systemPrompt) = Boot();
            try
            {
                foreach (var name in names)
                {
                    systemPrompt.RegisterSection(new PromptSection(name, 10, PromptText.Static(name)));
                }
                // 'z' (0x7A) sorts before 'ä' (0xE4) in code-unit order, regardless of registration order.
                Assert.Equal(new[] { "zeta", "äther" }, Contributed(await systemPrompt.AssembleAsync()).Select(s => s.Name).ToArray());
            }
            finally
            {
                ctx.Dispose();
            }
        }
    }

    public static async Task DisposeRemovesSectionFromLaterAssemblies()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var dispose = systemPrompt.RegisterSection(new PromptSection("scoped", 0, PromptText.Static("scoped section")));
            Assert.Equal(1, Contributed(await systemPrompt.AssembleAsync()).Count);
            dispose.Dispose();
            Assert.Equal(0, Contributed(await systemPrompt.AssembleAsync()).Count);
            // The built-ins belong to the service fiber, so they survive the section's disposal.
            Assert.Equal(new[] { "harness:identity", "deployment:persona" }, (await systemPrompt.AssembleAsync()).Sections.Select(s => s.Name).ToArray());
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task ContextDisposeRemovesTheBuiltInSections()
    {
        var ctx = new Context();
        var systemPrompt = new SystemPromptService(ctx);
        ctx.Dispose();
        Assert.Equal(0, (await systemPrompt.AssembleAsync()).Sections.Count);
    }

    public static void DuplicateSectionRegistrationThrows_WithoutLeaking()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            systemPrompt.RegisterSection(new PromptSection("dup", 0, PromptText.Static("first")));
            Assert.Throws<InvalidOperationException>(
                () => systemPrompt.RegisterSection(new PromptSection("dup", 1, PromptText.Static("second"))),
                "a duplicate section name must throw");
            // The failed registration leaked nothing; the original stays intact.
            Assert.Equal(new[] { "first" }, Contributed(systemPrompt.AssembleAsync().GetAwaiter().GetResult()).Select(s => s.Text).ToArray());
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task AssemblyOutputIsStable_PinnedExpectedText()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig { Persona = "You are the deployment assistant." });
        try
        {
            systemPrompt.RegisterSection(new PromptSection("empty", 100, PromptText.Static("")));
            systemPrompt.RegisterSection(new PromptSection("tool:bash", 1000, PromptText.Static("Prefer bash for file and process operations.")));
            var rendered = systemPrompt.RenderPrompt(await systemPrompt.AssembleAsync());
            Assert.Equal(
                "You are an AI agent powered by DeepSeek Harness.\n\n" +
                "You are the deployment assistant.\n\n" +
                "Prefer bash for file and process operations.",
                rendered);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task CustomSeparatorFromConfig_IsUsedForRendering()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig { Persona = "P", SectionSeparator = "\n---\n" });
        try
        {
            systemPrompt.RegisterSection(new PromptSection("s", 100, PromptText.Static("S")));
            Assert.Equal($"{Identity}\n---\nP\n---\nS", systemPrompt.RenderPrompt(await systemPrompt.AssembleAsync()));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void RenderPromptDropsEmptySections()
    {
        var assembly = new PromptAssembly(
            new[] { new AssembledSection("empty", ""), new AssembledSection("real", "content") },
            Array.Empty<ToolSchema>());
        Assert.Equal("content", PromptRendering.Render(assembly, "\n\n"));
    }

    public static void SectionOrderLookupMatchesCentralAllocation()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            Assert.Equal(-1000, systemPrompt.GetSectionOrder(SectionOrderName.HARNESS_IDENTITY));
            Assert.Equal(0, systemPrompt.GetSectionOrder(SectionOrderName.DEPLOYMENT_PERSONA));
            Assert.Equal(9900, systemPrompt.GetSectionOrder(SectionOrderName.STRUCTURED_OUTPUT));
            Assert.Equal(110, systemPrompt.GetContextOrder(ContextOrderName.SANDBOX_POLICY));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void SectionOrderNamesAreUniqueAndAtLeastTenApart()
    {
        var orders = Enum.GetValues<SectionOrderName>().Select(order => (int)order).OrderBy(value => value).ToArray();
        Assert.True(orders.Distinct().Count() == orders.Length, "section placements must be unique");
        for (var i = 1; i < orders.Length; i++)
        {
            Assert.True(orders[i] - orders[i - 1] >= 10, $"section placements must be at least ten apart (gap at {orders[i - 1]} -> {orders[i]})");
        }
        Assert.True(Enum.GetValues<ContextOrderName>().Select(order => (int)order).Distinct().Count() == Enum.GetValues<ContextOrderName>().Length,
            "context placements must be unique");
    }

    public static async Task ToolRegistrySchemasAppearInTheAssembledRequest()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var tools = new ToolRuntime(ctx);
            tools.Register(ToolDefinition("todo_write", "Record and update a structured task list."));
            var assembly = await systemPrompt.AssembleAsync();
            Assert.Equal(new[] { "todo_write" }, assembly.Tools.Select(t => t.Name).ToArray());
            Assert.Equal("Record and update a structured task list.", assembly.Tools[0].Description);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task AssemblyWithoutToolRegistryHasNoTools()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            Assert.Equal(0, (await systemPrompt.AssembleAsync()).Tools.Count);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task ToolSchemasOrderLexicographically_WithoutConfiguredOrder()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var tools = new ToolRuntime(ctx);
            tools.Register(ToolDefinition("charlie"));
            tools.Register(ToolDefinition("alpha"));
            Assert.Equal(new[] { "alpha", "charlie" }, (await systemPrompt.AssembleAsync()).Tools.Select(t => t.Name).ToArray());
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task ToolOrderAppliesConfiguredOrder_WithRestLexicographic()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig
        {
            ToolOrder = new[] { "todo_write", PromptConstants.ToolOrderRest, "bash" },
        });
        try
        {
            var tools = new ToolRuntime(ctx);
            tools.Register(ToolDefinition("bash"));
            tools.Register(ToolDefinition("echo_b"));
            tools.Register(ToolDefinition("todo_write"));
            tools.Register(ToolDefinition("echo_a"));
            Assert.Equal(
                new[] { "todo_write", "echo_a", "echo_b", "bash" },
                (await systemPrompt.AssembleAsync()).Tools.Select(t => t.Name).ToArray());
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void ToolOrderRejectsUnknownTool_AtAssembly()
    {
        var (ctx, systemPrompt) = Boot(new SystemPromptConfig
        {
            ToolOrder = new[] { "todo_write", "ghost", PromptConstants.ToolOrderRest, "wraith" },
        });
        try
        {
            var tools = new ToolRuntime(ctx);
            tools.Register(ToolDefinition("bash"));
            tools.Register(ToolDefinition("todo_write"));
            var error = Assert.Throws<InvalidOperationException>(
                () => systemPrompt.AssembleAsync().GetAwaiter().GetResult(),
                "an unknown configured tool must fail assembly");
            Assert.Equal("toolOrder lists unregistered tools \"ghost\", \"wraith\"; known tools: bash, todo_write", error.Message);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void ToolOrderValidation_RejectsShapeViolations_AtConstruction()
    {
        var ctx = new Context();
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => new SystemPromptService(ctx, new SystemPromptConfig { ToolOrder = new[] { "bash", "todo_write" } }),
                "a toolOrder without the rest entry must fail at construction");
            Assert.Throws<InvalidOperationException>(
                () => new SystemPromptService(ctx, new SystemPromptConfig { ToolOrder = new[] { "bash", "bash", PromptConstants.ToolOrderRest } }),
                "a duplicate toolOrder name must fail at construction");
            Assert.Throws<InvalidOperationException>(
                () => new SystemPromptService(ctx, new SystemPromptConfig { ToolOrder = new[] { PromptConstants.ToolOrderRest, "bash", PromptConstants.ToolOrderRest } }),
                "a duplicate rest entry must fail at construction");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task DisposingToolProviderRemovesItsSchemas()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var dispose = systemPrompt.RegisterToolProvider(_ => new ToolProviderResult(new[] { Tool("direct-tool") }));
            Assert.Equal(1, (await systemPrompt.AssembleAsync()).Tools.Count);
            dispose.Dispose();
            Assert.Equal(0, (await systemPrompt.AssembleAsync()).Tools.Count);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task SectionTextProvidersResolvePerAssembly()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var calls = 0;
            systemPrompt.RegisterSection(new PromptSection("dynamic", 0, PromptText.Provider(_ => $"call {++calls}")));
            Assert.Equal("call 1", (await systemPrompt.AssembleAsync()).Sections.Single(s => s.Name == "dynamic").Text);
            Assert.Equal("call 2", (await systemPrompt.AssembleAsync()).Sections.Single(s => s.Name == "dynamic").Text);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void ChangeEventEmittedOnRegisterAndDispose()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var changeCount = 0;
            ctx.On("system-prompt/change", () => changeCount++);
            var dispose = systemPrompt.RegisterToolProvider(_ => new ToolProviderResult(Array.Empty<ToolSchema>()));
            Assert.Equal(1, changeCount);
            dispose.Dispose();
            Assert.Equal(2, changeCount);
        }
        finally
        {
            ctx.Dispose();
        }
    }
}
