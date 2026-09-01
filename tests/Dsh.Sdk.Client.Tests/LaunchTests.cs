namespace Harness.Sdk.Client.Tests;

/// <summary>Pure launch-resolution tests (port of the TS launch spec): defaults and overrides.</summary>
public static class LaunchTests
{
    public static void ResolveLaunch_AppliesTheDefaults()
    {
        var runtime = SdkLaunch.ResolveLaunch(new HarnessClientOptions { DshBin = "C:\\dsh\\Harness.Cli.dll" }, "C:\\work");
        Assert.Equal("dotnet", runtime.Command, "a .dll entry spawns via dotnet");
        Assert.True(runtime.Args.SequenceEqual(new[] { "C:\\dsh\\Harness.Cli.dll", "--profile", "sdk" }), "the canonical argv");
        Assert.Equal("dsh profile \"sdk\"", runtime.Description, "the description names the default profile");
        Assert.Equal(10_000, runtime.InitializeTimeoutMs, "the default handshake bound");
        Assert.Null(runtime.RequestTimeoutMs, "no default request timeout");
        Assert.Equal(1000, runtime.ShutdownTimeoutMs, "the default shutdown bound");
        Assert.Equal(6000, runtime.DisposeEofGraceMs, "the default EOF grace");
        Assert.Equal(3000, runtime.DisposeGraceMs, "the default termination window");
        Assert.Null(runtime.WorkingDirectory, "no default process cwd");
    }

    public static void ResolveLaunch_AppliesTheOverrides()
    {
        var runtime = SdkLaunch.ResolveLaunch(new HarnessClientOptions
        {
            DshBin = "C:\\dsh\\dsh.exe",
            Profile = "acp",
            Patches = new[] { "patch-a.yml", "C:\\abs\\patch-b.yml" },
            DshHome = "home",
            ProcessCwd = "workdir",
            RequestTimeoutMs = 42,
            Env = new Dictionary<string, string> { ["ONLY_ME"] = "1" },
        }, "C:\\caller");
        Assert.Equal("C:\\dsh\\dsh.exe", runtime.Command, "an apphost entry spawns directly");
        Assert.True(runtime.Args.SequenceEqual(new[] { "--profile", "acp", "--patch", "C:\\caller\\patch-a.yml", "--patch", "C:\\abs\\patch-b.yml" }),
            "patches resolve against the caller cwd");
        Assert.Equal("C:\\caller\\home", runtime.Environment()["DSH_HOME"], "the home resolves and lands in the child env");
        Assert.Equal("C:\\caller\\workdir", runtime.WorkingDirectory, "the process cwd resolves against the caller cwd");
        Assert.Equal(42, runtime.RequestTimeoutMs, "the per-request bound flows through");
        var env = runtime.Environment();
        Assert.Equal(2, env.Count, "a provided env replaces the parent environment entirely");
        Assert.Equal("1", env["ONLY_ME"], "the provided entry survives");
        Assert.False(env.ContainsKey("PATH"), "no parent entries leak into a replaced environment");
    }

}
