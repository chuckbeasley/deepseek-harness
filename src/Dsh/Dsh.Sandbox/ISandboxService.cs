namespace Dsh.Sandbox;

/// <summary>
/// Service Definition for the same-world process-confinement capability seam (C# port of
/// <c>@deepseek-ai/dsh-sandbox</c> plus the policy VALUE surface of
/// <c>@deepseek-ai/dsh-sandbox-policy</c>): wrap exact subprocess argv under a host-path file
/// policy. Containers, microVMs, and remote execution replace the surrounding capability seam
/// instead; this service shares the host kernel and filesystem.
///
/// The port's honest surface is the policy VALUE plane: the deployment default mode, the explicit
/// resolve(request) policy step, and the run-facts description. Reductions vs the TS seam:
/// <list type="bullet">
/// <item><description><c>SandboxProvider.confine(argv, policy): ConfinedArgv</c> — wrapping exact
/// argv into enforcing runner argv — is deferred to the native-bridge track (the sidecar spawn
/// runner; its argv contract is documented on <see cref="UnsandboxedSandboxProvider"/>). The
/// unsandboxed provider never confines, so consumers that require a confining mode must fail
/// closed with <see cref="SandboxErrorCodes.Unavailable"/> at their own enforcement step.</description></item>
/// <item><description>The policy RESOLVER service (<c>SandboxPolicyService</c> /
/// <c>ctx.sandboxPolicy</c> — the deployment default, the session-override projection, and the
/// request-time resolution that combines them) is deferred; <see cref="ResolvePolicy"/> takes an
/// explicit request without a session.</description></item>
/// <item><description>The approval-channel escalation choreography (<c>approveEscalation</c>) is
/// deferred with the interaction seam; the port carries the escalation vocabulary
/// (<see cref="SandboxEscalation"/>) only.</description></item>
/// </list>
/// </summary>
public interface ISandboxService
{
    /// <summary>The deployment default mode — the fallback beneath a session override.</summary>
    SandboxMode DefaultMode { get; }

    /// <summary>
    /// Resolve the complete file-effect policy for one capability call: an approved explicit mode
    /// outranks the session's last override (deferred), which outranks <see cref="DefaultMode"/>.
    /// </summary>
    SandboxExecutionPolicy ResolvePolicy(SandboxPolicyRequest request);

    /// <summary>
    /// Describe the sandbox facts of one run this provider handled. Facts are reported
    /// independently of process exit status so callers distinguish command failures from policy
    /// denials and runner failures.
    /// </summary>
    ShellSandboxInfo DescribeRun(SandboxExecutionPolicy policy);
}
