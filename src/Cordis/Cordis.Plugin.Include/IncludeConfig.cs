namespace Harness.Cordis.Plugin.Include;

/// <summary>
/// Config for a file-backed loader subtree (port of the vendored Include config). The entry list
/// reads from <see cref="Path"/>; <see cref="Initial"/> seeds the file when it does not exist, and
/// <see cref="Patches"/> are runtime patch layers applied after every read.
/// </summary>
public sealed class IncludeConfig
{
    /// <summary>YAML or JSON file path.</summary>
    public string Path { get; set; } = "";

    /// <summary>Entry list written when the file does not already exist.</summary>
    public List<object?>? Initial { get; set; }

    /// <summary>Runtime patch layers applied to the entry list after each read, in order.</summary>
    public List<object?>? Patches { get; set; }

    /// <summary>Enables loader apply/reload/unload logs for this subtree.</summary>
    public bool EnableLogs { get; set; }

    /// <summary>Treat the file as JSON instead of YAML.</summary>
    public bool IsJson { get; set; }
}
