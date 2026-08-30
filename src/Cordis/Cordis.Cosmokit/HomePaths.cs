namespace Cordis.Cosmokit;

/// <summary>
/// Shared filesystem path helpers for DeepSeek Harness user data (port of
/// <c>@deepseek-ai/dsh-home-paths</c>).
/// </summary>
public static class HomePaths
{
    /// <summary>Directory name for the default harness home under the OS home.</summary>
    public const string DshHomeDirName = ".dsh";

    /// <summary>Stable user-facing display form for the default harness home.</summary>
    public const string DefaultDshHomeDisplay = "~/.dsh";

    /// <summary>Environment variable that overrides the default harness home.</summary>
    public const string DshHomeEnv = "DSH_HOME";

    /// <summary>Returns the default harness home path under the operating-system user profile.</summary>
    public static string DefaultDshHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, DshHomeDirName);
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
    /// explicit configured path, <c>$DSH_HOME</c>, then the default
    /// <c>~/.dsh</c>. An empty or whitespace-only <c>$DSH_HOME</c> is treated as
    /// unset, so a blank override never resolves the home to the current
    /// directory.
    /// </summary>
    /// <param name="configured">Explicit harness-home override, which has highest precedence.</param>
    /// <param name="env">Environment mapping used to read <c>DSH_HOME</c>; defaults to the process environment.</param>
    /// <returns>The normalized absolute harness home path.</returns>
    public static string ResolveDshHome(string? configured = null, IDictionary<string, string?>? env = null)
    {
        env ??= new Dictionary<string, string?>();
        var fromEnv = env.TryGetValue(DshHomeEnv, out var value) ? value : Environment.GetEnvironmentVariable(DshHomeEnv);
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
            selected = DefaultDshHome();
        }
        return Path.GetFullPath(ExpandHomePath(selected));
    }

    /// <summary>Joins path segments onto the resolved harness home; an empty list returns the home itself.</summary>
    public static string DshHomePath(params string[] segments)
        => Path.Combine([ResolveDshHome(), .. segments]);

    /// <summary>
    /// Describes a resolved harness home symbolically for user-facing display.
    /// The default home is labelled <c>~/.dsh</c>; any configured home is
    /// labelled <c>$DSH_HOME</c>. Never returns an absolute machine path.
    /// </summary>
    public static string DshHomeDisplay(string resolvedHome)
    {
        return string.Equals(resolvedHome, Path.GetFullPath(DefaultDshHome()), StringComparison.OrdinalIgnoreCase)
            ? DefaultDshHomeDisplay
            : "$" + DshHomeEnv;
    }
}
