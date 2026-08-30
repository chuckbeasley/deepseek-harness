namespace Dsh.Cli;

/// <summary>
/// Command-line entry for <c>dsh</c> (port of <c>apps/cli/src/bin.ts</c>): parse the launcher
/// flags, then dispatch the resolved invocation. The booted app owns process lifetime — the
/// headless one-shot exits through <c>appExit</c>; a long-running app blocks here by design.
/// </summary>
public static class Program
{
    /// <summary>The launcher version printed by <c>-V</c> (C# port of the package.json version).</summary>
    public const string Version = "0.2.0";

    public static int Main(string[] args)
    {
        try
        {
            var parse = Args.ParseDshArgs(args, Version);
            if (parse.Invocation is null) return parse.ExitCode;
            switch (parse.Invocation)
            {
                case DshInvocation.ProfileInvocation profile:
                {
                    var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ProfileBoot.RunProfileAsync(profile, code => exit.TrySetResult(code)).GetAwaiter().GetResult();
                    return exit.Task.GetAwaiter().GetResult();
                }
                case DshInvocation.DumpConfigInvocation dump:
                    DumpConfig.RunDumpConfig(dump.Profile, dump.DefaultOnly, dump.Patches);
                    return 0;
                case DshInvocation.PluginInvocation plugin:
                    return Plugin.RunPlugin(plugin.Profile, plugin.Args);
                default:
                    throw new InvalidOperationException("dsh: unhandled invocation mode");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"dsh: {error.Message}");
            return 1;
        }
    }
}
