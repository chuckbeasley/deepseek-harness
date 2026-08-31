namespace Dsh.Web.App;

/// <summary>
/// The shell's copy dictionary (the C# half of the "client UI copy is locale-owned" rule): every
/// product-visible string rides a typed dictionary and reaches components through
/// <see cref="T"/>. Two dictionaries ship — English and Simplified Chinese (the repo's bilingual
/// pair) — and a missing key falls back to English, then to the key itself, so a typo renders
/// visibly instead of silently. The active locale is negotiated from the browser's
/// Accept-Language header (see <see cref="Negotiate"/>), pinned per request by the shell's
/// locale middleware (<see cref="DshWebApp"/>), and carried into the interactive circuit through
/// <see cref="StateKey"/> so the prerendered shell and its circuit never disagree. The scoped
/// DI instance is a facade over <see cref="LocaleScope"/>: the page pins the language in
/// <c>OnInitialized</c>, after the component's injected services were resolved, so every
/// <see cref="T"/> call reads the language afresh.
/// </summary>
public sealed class WebLocale
{
    /// <summary>The locale id of the English dictionary.</summary>
    public const string EnglishLanguage = "en";

    /// <summary>The locale id of the Simplified Chinese dictionary.</summary>
    public const string ChineseLanguage = "zh";

    /// <summary>HttpContext.Items key carrying the negotiated locale id (see <see cref="DshWebApp"/>).</summary>
    public const string ItemsKey = "dsh.locale";

    /// <summary>PersistentComponentState key carrying the pinned locale across prerender and circuit.</summary>
    public const string StateKey = "dsh-locale";

    private readonly string? _fixedLanguage;
    private readonly IReadOnlyDictionary<string, string> _active;
    private readonly IReadOnlyDictionary<string, string> _fallback;
    private readonly LocaleScope? _scope;

    private WebLocale(string language, IReadOnlyDictionary<string, string> active, IReadOnlyDictionary<string, string> fallback)
    {
        _fixedLanguage = language;
        _active = active;
        _fallback = fallback;
    }

    /// <summary>Create the per-scope facade the DI resolves (the app project's scoped registration).</summary>
    internal WebLocale(LocaleScope scope)
    {
        _scope = scope;
        _active = EnglishStrings;
        _fallback = EnglishStrings;
    }

    /// <summary>The locale id this instance translates for.</summary>
    public string Language => _scope?.Language ?? _fixedLanguage!;

    /// <summary>
    /// Resolve one copy key: the active dictionary, then English, then the key itself. The scoped
    /// facade delegates to the shipped instance for the scope's current language, so the page's
    /// pin in <c>OnInitialized</c> always applies to the render that follows.
    /// </summary>
    public string T(string key)
    {
        if (_scope is not null) return _scope.Active.T(key);
        return _active.TryGetValue(key, out var text) || _fallback.TryGetValue(key, out text) ? text : key;
    }

    // The dictionaries are declared before the instances they feed: static initializers run in
    // declaration order, so an instance field must never precede the dictionary it reads.
    private static readonly IReadOnlyDictionary<string, string> EnglishStrings = new Dictionary<string, string>
    {
        ["sessions"] = "Sessions",
        ["noSessions"] = "No sessions yet. Send a message to start one.",
        ["newSession"] = "new session",
        ["newSessionAction"] = "New Session",
        ["selectSession"] = "Select a session to view its transcript.",
        ["messagePlaceholder"] = "Message dsh...",
        ["send"] = "Send",
        ["running"] = "running",
        ["queued"] = "queued",
        ["lastError"] = "last error:",
        ["workspaces"] = "Workspaces",
        ["noWorkspaces"] = "No workspaces yet.",
    };

    private static readonly IReadOnlyDictionary<string, string> ChineseStrings = new Dictionary<string, string>
    {
        ["sessions"] = "会话",
        ["noSessions"] = "暂无会话，发送一条消息即可开始。",
        ["newSession"] = "新会话",
        ["newSessionAction"] = "新建会话",
        ["selectSession"] = "选择一个会话查看记录。",
        ["messagePlaceholder"] = "给 dsh 发送消息…",
        ["send"] = "发送",
        ["running"] = "运行中",
        ["queued"] = "排队中",
        ["lastError"] = "最近错误：",
        ["workspaces"] = "工作区",
        ["noWorkspaces"] = "暂无工作区。",
    };

    /// <summary>The English dictionary.</summary>
    public static WebLocale English { get; } = new(EnglishLanguage, EnglishStrings, EnglishStrings);

    /// <summary>The Simplified Chinese dictionary; a key it lacks falls back to English.</summary>
    public static WebLocale Chinese { get; } = new(ChineseLanguage, ChineseStrings, EnglishStrings);

    /// <summary>
    /// Resolve the shipped locale for one Accept-Language header: the first language whose
    /// primary subtag matches a shipped locale wins; anything else (an absent, empty, or
    /// unmatched header) resolves to English. Preference follows header order, the shape every
    /// browser sends; q-values are not re-sorted.
    /// </summary>
    public static string Negotiate(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)) return EnglishLanguage;
        foreach (var part in acceptLanguage.Split(','))
        {
            var tag = part.Split(';')[0].Trim();
            if (tag.Length == 0) continue;
            var primary = tag.Split('-')[0];
            if (string.Equals(primary, ChineseLanguage, StringComparison.OrdinalIgnoreCase)) return ChineseLanguage;
            if (string.Equals(primary, EnglishLanguage, StringComparison.OrdinalIgnoreCase)) return EnglishLanguage;
        }
        return EnglishLanguage;
    }

    /// <summary>The shipped instance for one locale id; an unknown id resolves to English.</summary>
    public static WebLocale For(string language)
        => string.Equals(language, ChineseLanguage, StringComparison.OrdinalIgnoreCase) ? Chinese : English;
}
