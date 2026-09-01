using Harness.Llm;

namespace Harness.SystemPrompt;

/// <summary>
/// Context for one prompt assembly. The TS original is merge-extensible (dsh-agent adds the live
/// agent); the .NET port carries no fields yet — scope and per-agent facts arrive with the scope port.
/// </summary>
public sealed record AssembleContext
{
}

/// <summary>Section text: static, or resolved per assembly from that assembly's context.</summary>
public abstract record PromptText
{
    /// <summary>Create static section text.</summary>
    public static PromptText Static(string value) => new StaticText(value);

    /// <summary>Create section text resolved per assembly from the context.</summary>
    public static PromptText Provider(Func<AssembleContext, string> provider) => new ProviderText(provider);

    /// <summary>Resolve the section text for one assembly.</summary>
    public abstract string Resolve(AssembleContext context);

    private sealed record StaticText(string Value) : PromptText
    {
        public override string Resolve(AssembleContext context) => Value;
    }

    private sealed record ProviderText(Func<AssembleContext, string> Function) : PromptText
    {
        public override string Resolve(AssembleContext context) => Function(context);
    }
}

/// <summary>One contributed section of the system prompt (registry input).</summary>
/// <param name="Name">Unique name — a duplicate registration throws.</param>
/// <param name="Order">Sections are concatenated in ascending order; equal orders use code-unit name order.</param>
/// <param name="Text">Static text or a provider resolved per assembly with that assembly's context.</param>
public sealed record PromptSection(string Name, int Order, PromptText Text);

/// <summary>One section of an assembly: the contributing name with its text resolved (not yet rendered).</summary>
/// <param name="Name">The contributing section's unique name.</param>
/// <param name="Text">The resolved section text.</param>
public sealed record AssembledSection(string Name, string Text);

/// <summary>Tool schemas visible in one assembly and their pre-restriction name set.</summary>
/// <param name="Schemas">The schemas this provider contributes to THIS assembly.</param>
/// <param name="KnownNames">The pre-restriction name universe for toolOrder validation (defaults to <paramref name="Schemas"/>' names).</param>
public sealed record ToolProviderResult(IReadOnlyList<ToolSchema> Schemas, IReadOnlyList<string>? KnownNames = null);

/// <summary>Assembled model input: resolved sections and canonically ordered tool schemas.</summary>
/// <param name="Sections">Sections in canonical (order, name) sequence, resolved but not yet rendered.</param>
/// <param name="Tools">Tool schemas in canonical order, parameters detached from their sources.</param>
public sealed record PromptAssembly(IReadOnlyList<AssembledSection> Sections, IReadOnlyList<ToolSchema> Tools);

/// <summary>Pure rendering helpers for an assembled prompt.</summary>
public static class PromptRendering
{
    /// <summary>Drop empty sections and join the rest with <paramref name="separator"/>.</summary>
    public static string Render(PromptAssembly assembly, string separator)
        => string.Join(separator, assembly.Sections.Select(section => section.Text).Where(text => text.Length > 0));
}
