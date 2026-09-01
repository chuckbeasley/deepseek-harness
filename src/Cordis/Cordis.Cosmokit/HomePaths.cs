namespace Harness.Cordis.Cosmokit;

/// <summary>
/// Shared filesystem path helpers for DeepSeek Harness user data (port of
/// <c>@deepseek-ai/hsh-home-paths</c>).
/// </summary>
public static class HomePaths
{
    /// <summary>Directory name for the default harness home under the OS home.</summary>
    public const string HshHomeDirName = ".hsh";

    /// <summary>Stable user-facing display form for the default harness home.</summary>
    public const string DefaultHshHomeDisplay = "~/.hsh";

    /// <summary>Environment variable that overrides the default harness home.</summary>
    public const string HshHomeEnv = "HSH_HOME";

    /// <summary>Returns the default harness home path under the operating-system user profile.</summary>
    public static string DefaultHshHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, HshHomeDirName);
    }

    /// <summary>
    /// Expands supported tilde prefixes against the operating-system home:
    /// <c>~</c> alone, or <c>~/</c> / <c>~\</c> followed by a relative path.
    /// Returns the original value when no supported prefix is present.
    /// </summary>
    public static string ExpandHomePath(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }
        return path;
    }

    /// <summary>
    /// Resolves the single-root harness home. Precedence, highest first: an
    /// explicit configured path, <c>$HSH_HOME</c>, then the default
    /// <c>~/.hsh</c>. An empty or whitespace-only <c>$HSH_HOME</c> is treated as
    /// unset, so a blank override never resolves the home to the current
    /// directory.
    /// </summary>
    /// <param name="configured">Explicit harness-home override, which has highest precedence.</param>
    /// <param name="env">Environment mapping used to read <c>HSH_HOME</c>; defaults to the process environment.</param>
    /// <returns>The normalized absolute harness home path.</returns>
    public static string ResolveHshHome(string? configured = null, IDictionary<string, string?>? env = null)
    {
        env ??= new Dictionary<string, string?>();
        var fromEnv = env.TryGetValue(HshHomeEnv, out var value) ? value : Environment.GetEnvironmentVariable(HshHomeEnv);
        string selected;
        if (configured is not null)
        {
            selected = configured;
        }
        else if (fromEnv is not null && fromEnv.Trim().Length > 0)
        {
            selected = fromEnv;
        }
        else
        {
            selected = DefaultHshHome();
        }
        return Path.GetFullPath(ExpandHomePath(selected));
    }

    /// <summary>Joins path segments onto the resolved harness home; an empty list returns the home itself.</summary>
    public static string HshHomePath(params string[] segments)
        => Path.Combine([ResolveHshHome(), .. segments]);

    /// <summary>
    /// Describes a resolved harness home symbolically for user-facing display.
    /// The default home is labelled <c>~/.hsh</c>; any configured home is
    /// labelled <c>$HSH_HOME</c>. Never returns an absolute machine path.
    /// </summary>
    public static string HshHomeDisplay(string resolvedHome)
    {
        return string.Equals(resolvedHome, Path.GetFullPath(DefaultHshHome()), StringComparison.OrdinalIgnoreCase)
            ? DefaultHshHomeDisplay
            : "$" + HshHomeEnv;
    }
}
