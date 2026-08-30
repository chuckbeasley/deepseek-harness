namespace Cordis.Schemastery;

/// <summary>Runtime validation options shared by all schema calls.</summary>
public sealed class SchemaOptions
{
    /// <summary>Remove invalid object properties instead of throwing when possible.</summary>
    public bool Autofix { get; set; }

    /// <summary>Skip validation for selected values and schema nodes.</summary>
    public Func<object?, Schema, bool>? Ignore { get; set; }

    /// <summary>Path used to format nested validation errors (string and int segments).</summary>
    public List<object>? Path { get; set; }

    /// <summary>Returns a copy of these options with <paramref name="segment"/> appended to the path.</summary>
    internal SchemaOptions WithPath(object segment)
    {
        return new SchemaOptions
        {
            Autofix = Autofix,
            Ignore = Ignore,
            Path = Path is null ? new List<object> { segment } : new List<object>(Path) { segment },
        };
    }
}
