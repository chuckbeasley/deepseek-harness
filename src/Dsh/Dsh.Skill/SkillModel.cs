using System.Text.RegularExpressions;

namespace Dsh.Skill;

/// <summary>Skill-name grammar and validation (the TS SKILL_NAME kebab-case pattern).</summary>
public static class SkillNames
{
    private static readonly Regex Pattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>Return whether a string is a valid kebab-case skill name.</summary>
    public static bool IsSkillName(string name) => Pattern.IsMatch(name);
}

/// <summary>Resolved model and user invocation controls for one skill.</summary>
/// <param name="ModelInvocable">Whether model-facing catalogs and loaders include this skill.</param>
/// <param name="UserInvocable">Whether human-facing command catalogs and loaders include this skill.</param>
public sealed record SkillInvocationPolicy(bool ModelInvocable, bool UserInvocable);

/// <summary>Optional provider-specific base used by loaded skill bodies to resolve relative resources.</summary>
public abstract record SkillResourceBase
{
    /// <summary>The resource base kind tag: <c>directory</c>, <c>url</c>, or <c>opaque</c>.</summary>
    public abstract string Kind { get; }
}

/// <summary>A local directory base for relative resource resolution.</summary>
public sealed record SkillResourceDirectory(string Path) : SkillResourceBase
{
    /// <inheritdoc/>
    public override string Kind => "directory";
}

/// <summary>A URL base for relative resource resolution.</summary>
public sealed record SkillResourceUrl(string Url) : SkillResourceBase
{
    /// <inheritdoc/>
    public override string Kind => "url";
}

/// <summary>An opaque description of how this skill's resources are managed.</summary>
public sealed record SkillResourceOpaque(string Description) : SkillResourceBase
{
    /// <inheritdoc/>
    public override string Kind => "opaque";
}

/// <summary>Invocation-neutral skill metadata returned by the registry list.</summary>
/// <param name="Name">Kebab-case identifier used to address the skill.</param>
/// <param name="Description">Short routing description shown by discovery consumers.</param>
/// <param name="Invocation">Resolved model and user invocation controls.</param>
/// <param name="Source">Discovery source that produced this winning skill.</param>
/// <param name="Provider">Provider that owns this skill body.</param>
/// <param name="WhenToUse">Optional extra routing guidance.</param>
/// <param name="ResourceBase">Provider-specific base for relative resources.</param>
public record SkillSummary(
    string Name,
    string Description,
    SkillInvocationPolicy Invocation,
    string Source,
    string Provider,
    string? WhenToUse = null,
    SkillResourceBase? ResourceBase = null);

/// <summary>Provider catalog entry used by the registry to merge and later load skills.</summary>
/// <param name="Rank">Lower ranks win duplicate skill names before provider registration order is considered.</param>
/// <param name="Locator">Opaque provider-owned handle passed back to the provider's get.</param>
/// <param name="Path">Absolute file path when the provider has one.</param>
/// <param name="Metadata">Parsed optional metadata object from provider-specific skill frontmatter.</param>
public sealed record SkillCandidate(
    string Name,
    string Description,
    SkillInvocationPolicy Invocation,
    string Source,
    string Provider,
    int Rank,
    object Locator,
    string? WhenToUse = null,
    SkillResourceBase? ResourceBase = null,
    string? Path = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
    : SkillSummary(Name, Description, Invocation, Source, Provider, WhenToUse, ResourceBase);

/// <summary>Complete parsed skill definition, including the body loaded by the registry get.</summary>
/// <param name="Content">Markdown instruction body after any provider-specific metadata removal.</param>
public sealed record SkillDefinition(
    string Name,
    string Description,
    SkillInvocationPolicy Invocation,
    string Source,
    string Provider,
    string Content,
    string? WhenToUse = null,
    SkillResourceBase? ResourceBase = null,
    string? Path = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
    : SkillSummary(Name, Description, Invocation, Source, Provider, WhenToUse, ResourceBase);

/// <summary>Runtime skill contribution accepted by the registry register; omitted invocation and provider receive defaults.</summary>
/// <param name="Name">Kebab-case skill name.</param>
/// <param name="Description">Short routing description.</param>
/// <param name="Source">Discovery source this runtime contribution advertises.</param>
/// <param name="Content">Markdown instruction body.</param>
/// <param name="Invocation">Invocation controls; omission permits both model and user surfaces.</param>
/// <param name="Provider">Provider label; omission uses the registry-owned runtime provider.</param>
/// <param name="WhenToUse">Optional extra routing guidance.</param>
/// <param name="ResourceBase">Provider-specific base for relative resources.</param>
/// <param name="Path">Absolute file path when the skill came from disk.</param>
/// <param name="Metadata">Parsed optional metadata object from frontmatter.</param>
public sealed record SkillRegistration(
    string Name,
    string Description,
    string Source,
    string Content,
    SkillInvocationPolicy? Invocation = null,
    string? Provider = null,
    string? WhenToUse = null,
    SkillResourceBase? ResourceBase = null,
    string? Path = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>Caller context used for cwd-sensitive and cancellable provider work.</summary>
/// <param name="Cwd">Workspace selector for the current lookup.</param>
/// <param name="CancellationToken">Cancels discovery or loading work for the current caller.</param>
public sealed record SkillLookupOptions(string? Cwd = null, CancellationToken CancellationToken = default);
