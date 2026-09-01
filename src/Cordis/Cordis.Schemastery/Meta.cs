namespace Harness.Cordis.Schemastery;

/// <summary>A deprecated/experimental badge attached to schema metadata for form renderers.</summary>
/// <param name="Text">Badge label, e.g. <c>deprecated</c>.</param>
/// <param name="Type">Badge style, e.g. <c>danger</c> or <c>warning</c>.</param>
public sealed record Badge(string Text, string Type);

/// <summary>UI and validation metadata attached by schema builder methods.</summary>
public sealed class Meta
{
    /// <summary>Fallback value used for nullable input.</summary>
    public object? Default { get; set; }

    /// <summary>Whether nullable input is rejected.</summary>
    public bool Required { get; set; }

    /// <summary>Whether this node is disabled for form UIs.</summary>
    public bool Disabled { get; set; }

    /// <summary>Whether nested form UIs should render collapsed.</summary>
    public bool Collapse { get; set; }

    /// <summary>Whether this node is hidden from UI renderers.</summary>
    public bool Hidden { get; set; }

    /// <summary>Whether to return the default value instead of throwing when validation fails.</summary>
    public bool Loose { get; set; }

    /// <summary>Renderer role, e.g. <c>slider</c> or <c>datetime</c>.</summary>
    public string? Role { get; set; }

    /// <summary>Role-specific metadata.</summary>
    public object? Extra { get; set; }

    /// <summary>External documentation link.</summary>
    public string? Link { get; set; }

    /// <summary>A localized or plain description (a string, or a dictionary keyed by locale).</summary>
    public object? Description { get; set; }

    /// <summary>An auxiliary comment for documentation or form UIs.</summary>
    public string? Comment { get; set; }

    /// <summary>Regular expression constraint (source and flags) for strings.</summary>
    public (string Source, string Flags)? Pattern { get; set; }

    /// <summary>Inclusive maximum for numbers or collection lengths.</summary>
    public double? Max { get; set; }

    /// <summary>Inclusive minimum for numbers or collection lengths.</summary>
    public double? Min { get; set; }

    /// <summary>Numeric increment constraint.</summary>
    public double? Step { get; set; }

    /// <summary>Deprecated/experimental badges.</summary>
    public List<Badge>? Badges { get; set; }

    /// <summary>Creates empty metadata.</summary>
    public Meta()
    {
    }

    /// <summary>Copies all fields from <paramref name="other"/> (used by immutable builders).</summary>
    public Meta(Meta other)
    {
        Default = other.Default;
        Required = other.Required;
        Disabled = other.Disabled;
        Collapse = other.Collapse;
        Hidden = other.Hidden;
        Loose = other.Loose;
        Role = other.Role;
        Extra = other.Extra;
        Link = other.Link;
        Description = other.Description;
        Comment = other.Comment;
        Pattern = other.Pattern;
        Max = other.Max;
        Min = other.Min;
        Step = other.Step;
        Badges = other.Badges is null ? null : new List<Badge>(other.Badges);
    }

    /// <summary>
    /// Merges two metadata objects like the JS spread
    /// <c>{ ...outer, ...inner }</c>: <paramref name="inner"/> wins on every
    /// field.
    /// </summary>
    internal static Meta Merge(Meta outer, Meta inner)
    {
        var result = new Meta(outer);
        result.CopyFrom(inner);
        return result;
    }

    private void CopyFrom(Meta source)
    {
        Default = source.Default;
        Required = source.Required;
        Disabled = source.Disabled;
        Collapse = source.Collapse;
        Hidden = source.Hidden;
        Loose = source.Loose;
        Role = source.Role;
        Extra = source.Extra;
        Link = source.Link;
        Description = source.Description;
        Comment = source.Comment;
        Pattern = source.Pattern;
        Max = source.Max;
        Min = source.Min;
        Step = source.Step;
        Badges = source.Badges is null ? null : new List<Badge>(source.Badges);
    }
}
