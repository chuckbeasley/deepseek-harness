namespace Dsh.SystemPrompt;

/// <summary>
/// Deployment-authored system-prompt configuration. Shape violations fail loud at
/// <see cref="SystemPromptService"/> construction; unknown toolOrder names fail at assembly.
/// </summary>
public sealed record SystemPromptConfig
{
    /// <summary>Include the fixed harness identity before the deployment persona (default true).</summary>
    public bool IncludeHarnessIdentity { get; init; } = true;

    /// <summary>Deployment-wide order-0 persona template (default empty; rendered sections drop empty text).</summary>
    public string Persona { get; init; } = "";

    /// <summary>Separator joining rendered non-empty sections (default "\n\n", the TS renderPrompt separator).</summary>
    public string SectionSeparator { get; init; } = "\n\n";

    /// <summary>
    /// Model-facing tool names in order, with <see cref="PromptConstants.ToolOrderRest"/> exactly
    /// once. Omitted means lexicographic order; unknown names fail at assembly.
    /// </summary>
    public string[]? ToolOrder { get; init; }
}
