namespace Harness.Preset.Tests;

/// <summary>Coverage for the persona contribution of the preset seam.</summary>
public static class PersonaTests
{
    private static (Context Ctx, SystemPromptService SystemPrompt) Boot(string deploymentPersona = "deployment persona")
    {
        var ctx = new Context();
        return (ctx, new SystemPromptService(ctx, new SystemPromptConfig { Persona = deploymentPersona }));
    }

    public static void RegistersPersonaTextAsASection()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var provider = new PersonaProvider(systemPrompt);
            using var registration = provider.Register("You are the preset identity.");

            var assembly = systemPrompt.AssembleAsync().GetAwaiter().GetResult();

            var section = assembly.Sections.Single(s => s.Name == PersonaProvider.PersonaSectionName);
            Assert.Equal("You are the preset identity.", section.Text, "the registered text must appear verbatim");
            // The port cannot shadow the deployment slot without scoped contexts; both sections coexist.
            Assert.True(assembly.Sections.Any(s => s.Name == PromptConstants.PersonaSection && s.Text == "deployment persona"),
                "the deployment persona must remain registered beside the preset persona");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void DisposeRemovesTheSection()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var provider = new PersonaProvider(systemPrompt);
            var registration = provider.Register("You are the preset identity.");
            Assert.Equal(1, systemPrompt.AssembleAsync().GetAwaiter().GetResult().Sections
                .Count(s => s.Name == PersonaProvider.PersonaSectionName), "the section must be present while registered");

            registration.Dispose();

            Assert.Equal(0, systemPrompt.AssembleAsync().GetAwaiter().GetResult().Sections
                .Count(s => s.Name == PersonaProvider.PersonaSectionName), "disposing the registration must remove the section");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void EmptyTextDropsAtRender()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var provider = new PersonaProvider(systemPrompt);
            using var registration = provider.Register("");

            var assembly = systemPrompt.AssembleAsync().GetAwaiter().GetResult();
            Assert.Equal("", assembly.Sections.Single(s => s.Name == PersonaProvider.PersonaSectionName).Text,
                "empty persona text must register an empty section");

            var rendered = systemPrompt.RenderPrompt(assembly);
            Assert.Contains("deployment persona", rendered, "the deployment persona must still render");
            Assert.False(rendered.Contains("preset:persona", StringComparison.Ordinal), "the empty section must drop at render");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void DuplicateRegistrationFailsLoud()
    {
        var (ctx, systemPrompt) = Boot();
        try
        {
            var provider = new PersonaProvider(systemPrompt);
            using var first = provider.Register("first");

            Assert.Throws<InvalidOperationException>(
                () => provider.Register("second"),
                "a second persona registration must fail loud");

            var sections = systemPrompt.AssembleAsync().GetAwaiter().GetResult().Sections
                .Where(s => s.Name == PersonaProvider.PersonaSectionName).ToArray();
            Assert.Equal(1, sections.Length, "the failed registration must leak nothing");
            Assert.Equal("first", sections[0].Text, "the original registration must stay intact");
        }
        finally
        {
            ctx.Dispose();
        }
    }
}
