using Cordis.Core;

namespace Dsh.Sandbox;

/// <summary>Configuration for the unsandboxed backend: the fallback workspace root only.</summary>
public sealed record SandboxConfig(string? WorkspaceRoot = null);

/// <summary>
/// The mode-<c>none</c> provider (ctx.sandbox): never denies, never fails, and reports
/// <see cref="SandboxMode.None"/> facts. It is the honest baseline of the seam — every run it
/// handles executes unconfined — and the fallback composition until a confining backend lands.
/// <see cref="ResolvePolicy"/> therefore resolves mode <c>none</c> for every call: a caller that
/// requires confinement must fail closed at its own enforcement step rather than trust this
/// resolution.
///
/// The confining backends are deferred. The bwrap / Seatbelt / Windows-ACL runner family
/// (<c>@deepseek-ai/dsh-sandbox-local</c> in the TS repo) belongs to a later wave. The Landlock
/// sidecar spawn runner is owned by the native-bridge track; its argv contract
/// (native/landlock-run/docs/cli-contract.md) is:
/// <code>
/// landlock-run [--ro &lt;path&gt;]... [--rw &lt;path&gt;]... -- &lt;argv&gt;...
/// landlock-run --probe
/// </code>
/// <c>--ro</c> grants read + execute beneath a path; <c>--rw</c> grants full filesystem access
/// beneath it; everything not granted is denied (Landlock rulesets are allow-lists); <c>--</c> is
/// the mandatory separator, after which the wrapped argv is exec'd via <c>execvp</c> with the
/// launcher's environment unchanged; <c>--probe</c> is mutually exclusive with grants and a
/// command. Launcher-level failures — usage error, a kernel that cannot enforce Landlock, an
/// unopenable grant root, a failed exec — exit 125 (<c>LAUNCHER_FAILURE_EXIT</c>) with one stderr
/// line prefixed <c>landlock-run: </c> and the wrapped command does NOT run; the probe prints
/// <c>landlock: fully enforced</c> or <c>landlock: partially enforced (older ABI)</c> on stdout;
/// a confined run under a partial-ABI kernel prints <c>landlock-run: partial enforcement (older
/// Landlock ABI)</c> on stderr and proceeds, still confined for everything the kernel supports.
/// The future sidecar provider must fail closed: never pass a confined call through unconfined.
/// </summary>
public sealed class UnsandboxedSandboxProvider : Service, ISandboxService
{
    private readonly string _workspaceRoot;

    /// <summary>Register the provider as ctx.sandbox.</summary>
    public UnsandboxedSandboxProvider(Context ctx, SandboxConfig? config = null)
        : base(ctx, "sandbox")
    {
        _workspaceRoot = config?.WorkspaceRoot is string root
            ? SandboxRoots.CanonicalPath(root)
            : Environment.CurrentDirectory;
    }

    /// <inheritdoc />
    public SandboxMode DefaultMode => SandboxMode.None;

    /// <inheritdoc />
    public SandboxExecutionPolicy ResolvePolicy(SandboxPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new SandboxExecutionPolicy(
            SandboxMode.None,
            request.WorkspaceRoot is string root ? SandboxRoots.CanonicalPath(root) : _workspaceRoot);
    }

    /// <inheritdoc />
    public ShellSandboxInfo DescribeRun(SandboxExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new ShellSandboxInfo(SandboxMode.None, Denied: false);
    }
}
