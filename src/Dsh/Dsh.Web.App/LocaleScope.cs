namespace Dsh.Web.App;

/// <summary>
/// The per-scope active locale (scoped service): the page pins <see cref="Language"/> from the
/// request's Accept-Language during prerender and takes it back into the interactive circuit
/// through <see cref="WebLocale.StateKey"/> (PersistentComponentState), so the prerendered shell
/// and its circuit render in the same language. Components keep injecting <see cref="WebLocale"/>
/// — the DI factory resolves this scope's active instance.
/// </summary>
public sealed class LocaleScope
{
    /// <summary>The active locale id; English until the page pins it.</summary>
    public string Language { get; set; } = WebLocale.EnglishLanguage;

    /// <summary>The <see cref="WebLocale"/> instance translating for the active language.</summary>
    public WebLocale Active => WebLocale.For(Language);
}
