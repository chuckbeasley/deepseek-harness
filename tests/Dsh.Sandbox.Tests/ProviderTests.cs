using Cordis.Core;

namespace Dsh.Sandbox.Tests;

/// <summary>The unsandboxed provider's facts: mode none, never denied, never failed.</summary>
public static class ProviderTests
{
    public static void RegistersAsSandboxAndReportsNoneFacts()
    {
        using var ctx = new Context();
        var provider = new UnsandboxedSandboxProvider(ctx, new SandboxConfig());
        Assert.Same(provider, ctx.Get<ISandboxService>("sandbox"), "the provider registers as ctx.sandbox");
        Assert.Equal(SandboxMode.None, provider.DefaultMode, "the unsandboxed default is none");

        var policy = provider.ResolvePolicy(new SandboxPolicyRequest());
        Assert.Equal(SandboxMode.None, policy.Mode, "every resolved policy is none");
        Assert.True(Path.IsPathRooted(policy.WorkspaceRoot!), "the fallback workspace root resolves absolute");

        var facts = provider.DescribeRun(policy);
        Assert.Equal(SandboxMode.None, facts.Mode, "reports mode none");
        Assert.False(facts.Denied, "never denies");
        Assert.Null(facts.Enforcement, "no enforcement claim");
        Assert.Null(facts.RunnerFailed, "never fails");
    }

    public static void ResolvePolicyHonorsAnExplicitRoot()
    {
        using var ctx = new Context();
        var provider = new UnsandboxedSandboxProvider(ctx);
        var root = Path.GetFullPath(".");
        var policy = provider.ResolvePolicy(new SandboxPolicyRequest(WorkspaceRoot: root));
        Assert.Equal(root, policy.WorkspaceRoot, "an explicit root carries through");
    }
}