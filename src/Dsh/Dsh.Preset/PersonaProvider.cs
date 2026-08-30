using Dsh.SystemPrompt;

namespace Dsh.Preset;

/// <summary>
/// Persona text from a selected preset, contributed to the system prompt as an order-0 section
/// (port of the dsh-persona row). The TS row shadows the deployment persona by re-registering the
/// <c>deployment:persona</c> slot inside an agent scope; the port has no scoped contexts yet, so
/// the provider registers under its own section name at the deployment-persona placement, and a
/// duplicate registration fails loud exactly like an unscoped TS mount.
/// </summary>
public sealed class PersonaProvider
{
    /// <summary>The section name the preset persona registers under (the port's own slot; the
    /// <c>deployment:persona</c> slot is reserved by the prompt registry).</summary>
    public const string PersonaSectionName = "preset:persona";

    private readonly SystemPromptService _systemPrompt;

    /// <summary>Create the provider over the prompt registry.</summary>
    /// <param name="systemPrompt">the registry the persona section registers into.</param>
    public PersonaProvider(SystemPromptService systemPrompt)
    {
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
    }

    /// <summary>
    /// Register the persona text as the <see cref="PersonaSectionName"/> prompt section at the
    /// deployment-persona placement. The registration is an effect: disposing the returned
    /// disposer removes the section from later assemblies. Empty text registers an empty section,
    /// which is dropped when the prompt renders (matching the registry). Registering a second
    /// persona — or registering when the slot is already held — fails loud and leaks nothing.
    /// </summary>
    /// <param name="text">the persona prose.</param>
    /// <returns>the exact prompt-registry effect disposer.</returns>
    public IDisposable Register(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _systemPrompt.RegisterSection(new PromptSection(
            PersonaSectionName,
            (int)SectionOrderName.DEPLOYMENT_PERSONA,
            PromptText.Static(text)));
    }
}
