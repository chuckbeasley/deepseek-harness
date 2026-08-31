using Cordis.Core;
using Dsh.Sandbox;

namespace Dsh.Sandbox.Tests;

/// <summary>
/// The Landlock sidecar backend: argv wrapping per the sidecar contract, enforcement probing,
/// and the fail-closed refusal when the sidecar is unusable.
/// </summary>
public static class LandlockTests
{
    private const string Sidecar = "landlock-run";

    private static LandlockSidecarSandboxProvider Provider(string? sidecarPath, string? workspaceRoot = null)
    {
        var ctx = new Context();
        var provider = new LandlockSidecarSandboxProvider(ctx, new LandlockSidecarConfig(sidecarPath, workspaceRoot));
        return provider;
    }

    private static string TestAssembly => typeof(LandlockTests).Assembly.Location;

    private static string Wrapped(ConfinedArgv confined)
        => string.Join(" ", confined.Argv);

    public static void SidecarFullProbe_WrapsArgv()
    {
        var provider = Provider(TestAssembly);
        var policy = new SandboxExecutionPolicy(SandboxMode.ReadOnly, Path.GetFullPath("."));
        var confined = provider.Confine(new[] { "echo", "hi" }, policy);
        Assert.NotNull(confined, "a confining mode wraps");
        Assert.True(Wrapped(confined!).StartsWith("dotnet " + TestAssembly + " -- ", StringComparison.Ordinal),
            $"the managed sidecar runs under dotnet: {Wrapped(confined)}");
        Assert.True(Wrapped(confined).EndsWith("-- echo hi", StringComparison.Ordinal), "the command follows the separator");
        Assert.Equal(SandboxEnforcement.Full, confined!.Info.Enforcement, "a full probe reports full enforcement");
        Assert.Equal(SandboxMode.ReadOnly, confined.Info.Mode);
        Assert.False(confined.Info.Denied);
    }

    public static void SidecarPartialProbe_ReportsPartialEnforcement()
    {
        var previous = Environment.GetEnvironmentVariable("FAKE_PROBE_OUTPUT");
        try
        {
            Environment.SetEnvironmentVariable("FAKE_PROBE_OUTPUT", "landlock: partially enforced (older ABI)");
            var provider = Provider(TestAssembly);
            var confined = provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.WorkspaceWrite, Path.GetFullPath(".")));
            Assert.Equal(SandboxEnforcement.Partial, confined!.Info.Enforcement, "a partial probe reports partial enforcement");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_PROBE_OUTPUT", previous);
        }
    }

    public static void MissingSidecar_FailsClosed()
    {
        var provider = Provider(Path.Combine(Path.GetTempPath(), "no-such-landlock-run.exe"));
        var error = Assert.Throws<SandboxError>(() =>
            provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.ReadOnly, null)));
        Assert.Equal("SANDBOX_UNAVAILABLE", error.Code);
        Assert.True(error.Message.Contains("refusing to run the command unconfined", StringComparison.Ordinal), "the fail-closed text is verbatim");
    }

    public static void WorkspaceWrite_GrantsWritableRoots()
    {
        using var temp = new TempDir();
        var workspace = System.IO.Path.Combine(temp.Path, "ws");
        Directory.CreateDirectory(workspace);
        var provider = Provider(TestAssembly, workspace);
        var confined = provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.WorkspaceWrite, workspace));
        var wrapped = Wrapped(confined!);
        Assert.True(wrapped.Contains("--rw " + workspace, StringComparison.Ordinal), "the workspace root is granted");
        Assert.True(wrapped.IndexOf("--rw", StringComparison.Ordinal) < wrapped.IndexOf(" -- ", StringComparison.Ordinal),
            "grants precede the separator");
    }

    public static void ReadOnly_GrantsNothing()
    {
        var provider = Provider(TestAssembly);
        var confined = provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.ReadOnly, null));
        Assert.False(Wrapped(confined!).Contains("--rw"), "read-only grants nothing");
        Assert.False(Wrapped(confined).Contains("--ro"), "read-only grants nothing");
    }

    public static void UnconfinedModes_ReturnNull()
    {
        var provider = Provider(TestAssembly);
        Assert.Null(provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.None, null)), "none runs as-is");
        Assert.Null(provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.DangerFullAccess, null)), "full access runs as-is");
    }

    public static void UnsandboxedProvider_FailsClosedOnConfiningModes()
    {
        var ctx = new Context();
        var provider = new UnsandboxedSandboxProvider(ctx, new SandboxConfig());
        Assert.Null(provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.None, null)), "none runs as-is");
        var error = Assert.Throws<SandboxError>(() =>
            provider.Confine(new[] { "cmd" }, new SandboxExecutionPolicy(SandboxMode.WorkspaceWrite, null)));
        Assert.Equal("SANDBOX_UNAVAILABLE", error.Code);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-sandbox-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // already gone
            }
        }
    }
}
