namespace Dsh.Skill;

/// <summary>Provider interface for one source of skills, such as a local directory or a remote registry.</summary>
public interface ISkillProvider
{
    /// <summary>Unique provider name in the skill registry.</summary>
    string Name { get; }

    /// <summary>List available skill candidates for the current lookup context.</summary>
    /// <param name="options">Lookup options; <c>Cwd</c> selects workspace-sensitive skills and <c>CancellationToken</c> cancels work.</param>
    /// <returns>Provider candidates.</returns>
    Task<IReadOnlyList<SkillCandidate>> ListAsync(SkillLookupOptions options);

    /// <summary>Load a complete skill body for a previously listed candidate.</summary>
    /// <param name="candidate">The winning candidate originally returned by this provider.</param>
    /// <param name="options">Lookup options; <c>CancellationToken</c> cancels work.</param>
    /// <returns>The full skill body, or <c>null</c> if it is no longer loadable.</returns>
    Task<SkillDefinition?> GetAsync(SkillCandidate candidate, SkillLookupOptions options);
}
