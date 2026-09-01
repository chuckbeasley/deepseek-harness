namespace Dsh.Cli.Tests;

/// <summary>The launcher-args acceptance and rejection cases (port of apps/cli/tests/args.spec.ts).</summary>
public static class ArgsTests
{
    public static void BareInvocation_RequiresAProfile()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(Array.Empty<string>(), "9.9.9");
        Assert.Equal(1, result.ExitCode, "a bare invocation exits 1");
        Assert.True(result.Invocation is null, "a bare invocation resolves no invocation");
        Assert.Equal("error: --profile <name> is required", console.Error.ToString().TrimEnd(), "the exact error string");
    }

    public static void BareHelp_PrintsTheLauncherHelp()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "-h" }, "9.9.9");
        Assert.Equal(0, result.ExitCode, "help exits 0");
        Assert.True(console.Out.ToString().Contains("Usage: dsh"), "help prints the launcher usage");
    }

    public static void Version_PrintsAndExitsZero()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "-V" }, "9.9.9");
        Assert.Equal(0, result.ExitCode, "version exits 0");
        Assert.Equal("9.9.9", console.Out.ToString().TrimEnd(), "version prints the given string");
    }

    public static void ProfileBoot_PassesInnerArgumentsThrough()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--profile", "tui", "--resume", "abc" }, "9.9.9");
        var invocation = AssertInvocation<DshInvocation.ProfileInvocation>(result);
        Assert.Equal("tui", invocation.Profile, "the profile name");
        Assert.Sequence(new[] { "--resume", "abc" }, invocation.Args, "inner arguments pass through verbatim");
    }

    public static void ProfileHelp_PassesThroughToTheApp()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--profile", "web", "-h" }, "9.9.9");
        var invocation = AssertInvocation<DshInvocation.ProfileInvocation>(result);
        Assert.Sequence(new[] { "-h" }, invocation.Args, "with a profile, -h belongs to the app");
    }

    public static void Patches_CollectInOrder()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--patch", "a.yml", "--patch", "b.yml", "--profile", "headless" }, "9.9.9");
        var invocation = AssertInvocation<DshInvocation.ProfileInvocation>(result);
        Assert.Sequence(new[] { "a.yml", "b.yml" }, invocation.Patches, "patches collect in argv order");
    }

    public static void WebAlias_FixesTheProfile()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "web", "extra" }, "9.9.9");
        var invocation = AssertInvocation<DshInvocation.ProfileInvocation>(result);
        Assert.Equal("web", invocation.Profile, "web is the hardcoded profile alias");
        Assert.Sequence(new[] { "extra" }, invocation.Args, "web passes its inner arguments through");
    }

    public static void DumpConfig_MutualExclusion()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--profile", "headless", "--dump-config", "--dump-default-config" }, "9.9.9");
        Assert.Equal(1, result.ExitCode, "both dumps exit 1");
        Assert.Equal("error: --dump-config and --dump-default-config are mutually exclusive", console.Error.ToString().TrimEnd(), "the exact error string");
    }

    public static void DumpConfig_TakesNoAppArguments()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--profile", "headless", "--dump-config", "extra" }, "9.9.9");
        Assert.Equal(1, result.ExitCode, "a dump with app arguments exits 1");
        Assert.True(console.Error.ToString().Contains("error: config dumps take no app arguments, got \"extra\""), "the exact error string");
    }

    public static void DumpDefaultConfig_TakesNoPatches()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--profile", "headless", "--dump-default-config", "--patch", "x.yml" }, "9.9.9");
        Assert.Equal(1, result.ExitCode, "the default dump with an overlay exits 1");
        Assert.Equal("error: --dump-default-config prints the bundle layers and takes no --patch", console.Error.ToString().TrimEnd(), "the exact error string");
    }

    public static void Plugin_RejectsParentOptions()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "--profile", "tui", "plugin", "--profile", "tui", "add", "pkg" }, "9.9.9");
        Assert.Equal(1, result.ExitCode, "parent options before a subcommand exit 1");
        Assert.Equal("error: plugin takes none of parent --profile, --patch, --dump-config, or --dump-default-config", console.Error.ToString().TrimEnd(), "the exact error string");
    }

    public static void Plugin_RequiresProfileAndArgs()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "plugin", "add", "pkg" }, "9.9.9");
        Assert.Equal(1, result.ExitCode, "plugin without --profile exits 1");
        Assert.Equal("error: required option '--profile <name>' not specified", console.Error.ToString().TrimEnd(), "the exact error string");
        console.Dispose();
        using var second = new ConsoleCapture();
        result = Args.ParseDshArgs(new[] { "plugin", "--profile", "tui" }, "9.9.9");
        Assert.Equal(1, result.ExitCode, "plugin without arguments exits 1");
        Assert.Equal("error: plugin needs an action (add <bundle>, remove <bundle>, list)", second.Error.ToString().TrimEnd(), "the exact error string");
    }

    public static void Plugin_ResolvesAnInvocation()
    {
        using var console = new ConsoleCapture();
        var result = Args.ParseDshArgs(new[] { "plugin", "--profile", "tui", "add", "pkg" }, "9.9.9");
        var invocation = AssertInvocation<DshInvocation.PluginInvocation>(result);
        Assert.Equal("tui", invocation.Profile, "the plugin profile");
        Assert.Sequence(new[] { "add", "pkg" }, invocation.Args, "the plugin arguments");
    }

    private static T AssertInvocation<T>(ParseResult result) where T : DshInvocation
    {
        Assert.Equal(0, result.ExitCode, "the parse exits 0");
        Assert.True(result.Invocation is T, $"the invocation is a {typeof(T).Name}");
        return (T)result.Invocation!;
    }
}
