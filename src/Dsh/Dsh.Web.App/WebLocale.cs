namespace Dsh.Web.App;

/// <summary>
/// The shell's copy dictionary (the C# half of the "client UI copy is locale-owned" rule): every
/// product-visible string rides a typed dictionary and reaches components through
/// <see cref="T"/>. English is the only shipped locale for now — the locale-selection machinery
/// and further dictionaries are deferred — and a missing key falls back to the key itself so a
/// typo renders visibly instead of silently.
/// </summary>
public sealed class WebLocale
{
    private readonly IReadOnlyDictionary<string, string> _strings;

    private WebLocale(IReadOnlyDictionary<string, string> strings)
    {
        _strings = strings;
    }

    /// <summary>Resolve one copy key.</summary>
    public string T(string key) => _strings.TryGetValue(key, out var text) ? text : key;

    /// <summary>The English dictionary (the only shipped locale).</summary>
    public static WebLocale English { get; } = new(new Dictionary<string, string>
    {
        ["sessions"] = "Sessions",
        ["noSessions"] = "No sessions yet. Send a message to start one.",
        ["newSession"] = "new session",
        ["selectSession"] = "Select a session to view its transcript.",
        ["messagePlaceholder"] = "Message dsh...",
        ["send"] = "Send",
        ["running"] = "running",
        ["queued"] = "queued",
        ["lastError"] = "last error:",
    });
}
