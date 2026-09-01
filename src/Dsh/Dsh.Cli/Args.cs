namespace Dsh.Cli;

/// <summary>One resolved <c>dsh</c> invocation (port of the TS <c>DshInvocation</c> union).</summary>
public abstract record DshInvocation
{
    /// <summary>Boot a named profile and hand it the invocation's inner arguments.</summary>
    public sealed record ProfileInvocation(string Profile, IReadOnlyList<string> Patches, IReadOnlyList<string> Args) : DshInvocation;

    /// <summary>Print a composed profile tree and exit without booting.</summary>
    public sealed record DumpConfigInvocation(string Profile, bool DefaultOnly, IReadOnlyList<string> Patches) : DshInvocation;

    /// <summary>Manage a profile's plugins over its manifest.</summary>
    public sealed record PluginInvocation(string Profile, IReadOnlyList<string> Args) : DshInvocation;
}

/// <summary>Outcome of one argv parse: a resolved invocation, or an exit the parser already printed.</summary>
public sealed record ParseResult(DshInvocation? Invocation, int ExitCode)
{
    /// <summary>Successful parse; the caller runs the invocation.</summary>
    public static ParseResult Ok(DshInvocation invocation) => new(invocation, 0);

    /// <summary>Help, version, or an error already printed; the caller exits with <paramref name="code"/>.</summary>
    public static ParseResult Exit(int code) => new(null, code);
}

/// <summary>Launcher flags collected while scanning the parser's own window.</summary>
internal sealed class LauncherOptions
{
    public string? Profile;

    public List<string> Patches { get; } = new();

    public bool DumpConfig;

    public bool DumpDefaultConfig;

    /// <summary>Pass-through arguments after the launcher window; the booted app owns them verbatim.</summary>
    public List<string> Inner { get; } = new();
}

/// <summary>
/// The launcher's own argument parser (port of the TS commander adapter in
/// <c>apps/cli/src/args.ts</c>). The launcher parses only what it owns — which profile to boot,
/// which extra patch overlays to apply, and the config dumps — and hands everything after its own
/// flags to the booted tree verbatim. Launcher flags therefore come first: the first token this
/// parser does not recognize starts the inner arguments, so <c>dsh --profile web -h</c> prints the
/// web app's help, not this one's. <c>web</c> is a hardcoded alias for <c>--profile web</c>;
/// <c>plugin</c> manages a profile's plugin list. Commander is replaced by a hand-rolled scanner
/// (zero NuGet) with the TS's exact error strings and exit codes.
/// </summary>
public static class Args
{
    private const string ProfileOption = "--profile";
    private const string ProfileOptionEq = "--profile=";
    private const string PatchOption = "--patch";
    private const string PatchOptionEq = "--patch=";

    private const string Description =
        "dsh: boot a DeepSeek Harness profile — an ordered stack of plugin-bundle patch layers under your own overrides.";

    private const string HelpExamples = """
        Examples:
          dsh --profile web                          boot the web profile (same as: dsh web)
          dsh --profile headless "run the tests"     answer one task, print the result, and exit
          dsh --profile tui --patch ./extra.yml      boot a custom profile with one extra overlay
          dsh --profile tui --resume <session>       arguments after the launcher flags reach the app
          dsh --profile web --help                   the web app's own flags and help
          dsh plugin --profile tui add <package>     install a plugin into the tui profile
        """;

    /// <summary>
    /// Resolve argv into one invocation, or print and exit for help, version, or an error.
    /// </summary>
    /// <param name="argv">arguments after the executable name.</param>
    /// <param name="version">version string printed by <c>--version</c>.</param>
    /// <returns>the resolved invocation, or an exit code the caller must propagate.</returns>
    public static ParseResult ParseDshArgs(IReadOnlyList<string> argv, string version)
    {
        var options = new LauncherOptions();
        var i = 0;
        while (i < argv.Count)
        {
            var token = argv[i];
            if (token == ProfileOption)
            {
                if (i + 1 >= argv.Count) return Error($"error: option '{ProfileOption} <name>' argument missing");
                options.Profile = argv[i + 1];
                i += 2;
            }
            else if (token.StartsWith(ProfileOptionEq, StringComparison.Ordinal))
            {
                options.Profile = token[ProfileOptionEq.Length..];
                i++;
            }
            else if (token == PatchOption)
            {
                if (i + 1 >= argv.Count) return Error($"error: option '{PatchOption} <path>' argument missing");
                options.Patches.Add(argv[i + 1]);
                i += 2;
            }
            else if (token.StartsWith(PatchOptionEq, StringComparison.Ordinal))
            {
                options.Patches.Add(token[PatchOptionEq.Length..]);
                i++;
            }
            else if (token == "--dump-config")
            {
                options.DumpConfig = true;
                i++;
            }
            else if (token == "--dump-default-config")
            {
                options.DumpDefaultConfig = true;
                i++;
            }
            else if (token == "-V" || token == "--version")
            {
                Console.Out.WriteLine(version);
                return ParseResult.Exit(0);
            }
            else if (token == "web" || token == "plugin")
            {
                // The first non-option token that names a subcommand dispatches to it, even when
                // launcher options were parsed before it (commander's subcommand dispatch).
                var rest = argv.Skip(i + 1).ToArray();
                return token == "web" ? RunWeb(rest, options) : RunPluginCommand(rest, options);
            }
            else
            {
                // Unknown option or positional: the launcher window ends here and the app owns
                // everything from this token on, verbatim, including its own -h.
                options.Inner.AddRange(argv.Skip(i));
                break;
            }
        }
        return ResolveRoot(options);
    }

    /// <summary>The root action: help for a bare invocation, then the profile boot or dump.</summary>
    private static ParseResult ResolveRoot(LauncherOptions options)
    {
        if (options.Profile is null)
        {
            // With the app owning -h, the launcher's own help is what a bare `dsh -h` (no profile
            // to hand it to) must print.
            if (options.Inner.Any(argument => argument == "-h" || argument == "--help"))
            {
                WriteHelp();
                return ParseResult.Exit(0);
            }
            return Error("error: --profile <name> is required");
        }
        if (options.Profile.Length == 0) return Error("error: --profile needs a name");
        return ResolveBoot(options.Profile, options);
    }

    /// <summary>Resolve a boot or dump invocation from the launcher flags and the inner arguments.</summary>
    private static ParseResult ResolveBoot(string profile, LauncherOptions options)
    {
        var patches = options.Patches;
        if (patches.Contains("")) return Error("error: --patch needs a path");
        if (!options.DumpConfig && !options.DumpDefaultConfig)
        {
            return ParseResult.Ok(new DshInvocation.ProfileInvocation(profile, patches.ToArray(), options.Inner.ToArray()));
        }
        if (options.DumpConfig && options.DumpDefaultConfig)
        {
            return Error("error: --dump-config and --dump-default-config are mutually exclusive");
        }
        // The dump is boot-free: it never runs app command-line providers, so it cannot show what
        // those flags would decide, and printing a tree that differs from the same invocation's
        // boot would mislead.
        if (options.Inner.Count > 0)
        {
            return Error("error: config dumps take no app arguments, got "
                + string.Join(" ", options.Inner.Select(JsonQuote)));
        }
        var defaultOnly = options.DumpDefaultConfig;
        if (defaultOnly && patches.Count > 0)
        {
            return Error("error: --dump-default-config prints the bundle layers and takes no --patch");
        }
        return ParseResult.Ok(new DshInvocation.DumpConfigInvocation(profile, defaultOnly, patches.ToArray()));
    }

    /// <summary>The <c>web</c> subcommand: the profile is fixed to <c>web</c>.</summary>
    private static ParseResult RunWeb(IReadOnlyList<string> argv, LauncherOptions parent)
    {
        if (HasParentOptions(parent))
        {
            return Error("error: web takes none of parent --profile, --patch, --dump-config, or --dump-default-config");
        }
        var options = new LauncherOptions();
        var i = 0;
        while (i < argv.Count)
        {
            var token = argv[i];
            if (token == PatchOption)
            {
                if (i + 1 >= argv.Count) return Error($"error: option '{PatchOption} <path>' argument missing");
                options.Patches.Add(argv[i + 1]);
                i += 2;
            }
            else if (token.StartsWith(PatchOptionEq, StringComparison.Ordinal))
            {
                options.Patches.Add(token[PatchOptionEq.Length..]);
                i++;
            }
            else if (token == "--dump-config")
            {
                options.DumpConfig = true;
                i++;
            }
            else if (token == "--dump-default-config")
            {
                options.DumpDefaultConfig = true;
                i++;
            }
            else
            {
                options.Inner.AddRange(argv.Skip(i));
                break;
            }
        }
        return ResolveBoot("web", options);
    }

    /// <summary>The <c>plugin</c> subcommand: required <c>--profile</c>, everything else forwarded verbatim.</summary>
    private static ParseResult RunPluginCommand(IReadOnlyList<string> argv, LauncherOptions parent)
    {
        if (HasParentOptions(parent))
        {
            return Error("error: plugin takes none of parent --profile, --patch, --dump-config, or --dump-default-config");
        }
        string? profile = null;
        var args = new List<string>();
        var i = 0;
        while (i < argv.Count)
        {
            var token = argv[i];
            // The plugin subcommand keeps commander's default help option (only the root and web
            // disable it), so -h/--help print its own help and exit 0.
            if (token == "-h" || token == "--help")
            {
                WritePluginHelp();
                return ParseResult.Exit(0);
            }
            if (token == ProfileOption)
            {
                if (i + 1 >= argv.Count) return Error($"error: option '{ProfileOption} <name>' argument missing");
                profile = argv[i + 1];
                i += 2;
            }
            else if (token.StartsWith(ProfileOptionEq, StringComparison.Ordinal))
            {
                profile = token[ProfileOptionEq.Length..];
                i++;
            }
            else
            {
                args.Add(token);
                i++;
            }
        }
        if (profile is null) return Error("error: required option '--profile <name>' not specified");
        if (profile.Length == 0) return Error("error: --profile needs a name");
        if (args.Count == 0) return Error("error: plugin needs an action (add <bundle>, remove <bundle>, list)");
        return ParseResult.Ok(new DshInvocation.PluginInvocation(profile, args));
    }

    private static bool HasParentOptions(LauncherOptions parent)
        => parent.Profile is not null || parent.Patches.Count > 0 || parent.DumpConfig || parent.DumpDefaultConfig;

    /// <summary>Commander's <c>program.error</c>: the message is written verbatim (callers prefix <c>error: </c>), exit 1.</summary>
    private static ParseResult Error(string message)
    {
        Console.Error.WriteLine(message);
        return ParseResult.Exit(1);
    }

    /// <summary>JSON.stringify of one argument, as the TS dump-argument error renders.</summary>
    private static string JsonQuote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The launcher's own help text; each app prints its own.</summary>
    private static void WriteHelp()
    {
        Console.Out.WriteLine("Usage: dsh [options] [args...]");
        Console.Out.WriteLine();
        Console.Out.WriteLine(Description);
        Console.Out.WriteLine();
        Console.Out.WriteLine("Arguments:");
        Console.Out.WriteLine("  args  arguments for the booted profile's app (see: dsh --profile <name> --help)");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  -V, --version         output the version number");
        Console.Out.WriteLine("  --profile <name>      the profile under $DSH_HOME/profiles to boot");
        Console.Out.WriteLine("  --patch <path>        extra patch-list overlay applied after the profile layer (repeatable)");
        Console.Out.WriteLine("  --dump-config         print the composed profile tree and exit");
        Console.Out.WriteLine("  --dump-default-config print the profile tree without its user layer or --patch overlays and exit");
        Console.Out.WriteLine();
        Console.Out.WriteLine(HelpExamples.TrimEnd());
    }

    /// <summary>The plugin subcommand's own help (commander renders it for the registered subcommand).</summary>
    private static void WritePluginHelp()
    {
        Console.Out.WriteLine("Usage: dsh plugin [options] [args...]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("manage a profile's plugin bundles by editing its profile.json manifest");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Arguments:");
        Console.Out.WriteLine("  args  the action and its bundle argument (add <bundle>, remove <bundle>, list)");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --profile <name>  the profile whose plugins to manage (initialized on first use)");
        Console.Out.WriteLine("  -h, --help        display help for command");
    }
}
