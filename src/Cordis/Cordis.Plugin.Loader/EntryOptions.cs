namespace Harness.Cordis.Plugin.Loader;

/// <summary>
/// Serialized plugin entry options (C# port of the vendored loader <c>EntryOptions</c>). One value
/// describes one configured row of an <see cref="EntryTree"/>: the plugin to mount, its config, and
/// the metadata that gates mounting.
/// </summary>
public sealed class EntryOptions
{
    /// <summary>Stable id inside the containing entry tree; filled by <see cref="EntryTree.EnsureId"/> when empty.</summary>
    public string Id { get; set; } = "";

    /// <summary>Plugin specifier imported by the entry tree (a catalog name or a <c>cordis:</c> builtin).</summary>
    public string Name { get; set; } = "";

    /// <summary>Config passed to the plugin.</summary>
    public object? Config { get; set; }

    /// <summary>Marks this entry as a nested group; group rows mount <see cref="GroupPlugin"/> and are always enabled.</summary>
    public bool? Group { get; set; }

    /// <summary>Prevents this entry and its descendants from running.</summary>
    public bool? Disabled { get; set; }

    /// <summary>
    /// Required service names for this entry; its fiber stays <see cref="FiberState.Pending"/> until
    /// every one is registered on the shared context (port of the vendored <c>inject</c> service
    /// dependency list; intercept configuration is not ported).
    /// </summary>
    public IReadOnlyList<string>? Inject { get; set; }

    /// <summary>Deep copy used for legacy snapshots during transactional updates.</summary>
    public EntryOptions Clone() => new()
    {
        Id = Id,
        Name = Name,
        Config = Config,
        Group = Group,
        Disabled = Disabled,
        Inject = Inject,
    };
}
