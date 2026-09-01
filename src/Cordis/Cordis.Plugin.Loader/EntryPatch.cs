namespace Harness.Cordis.Plugin.Loader;

/// <summary>
/// Partial entry options used to update one row (port of the TS <c>Partial&lt;EntryOptions&gt;</c>
/// update argument). Id, Name, Group, Disabled and Inject apply only when non-null;
/// <see cref="Config"/> always applies, so a null value clears the config.
/// </summary>
public sealed class EntryPatch
{
    /// <summary>New local id; applied only when non-null.</summary>
    public string? Id { get; set; }

    /// <summary>New plugin specifier; applied only when non-null.</summary>
    public string? Name { get; set; }

    /// <summary>New config; always applied (null clears it).</summary>
    public object? Config { get; set; }

    /// <summary>New group marker; applied only when non-null.</summary>
    public bool? Group { get; set; }

    /// <summary>New disabled marker; applied only when non-null.</summary>
    public bool? Disabled { get; set; }

    /// <summary>New inject list; applied only when non-null.</summary>
    public IReadOnlyList<string>? Inject { get; set; }

    internal void ApplyTo(EntryOptions target)
    {
        if (Id is not null) target.Id = Id;
        if (Name is not null) target.Name = Name;
        target.Config = Config;
        if (Group is not null) target.Group = Group;
        if (Disabled is not null) target.Disabled = Disabled;
        if (Inject is not null) target.Inject = Inject;
    }
}
