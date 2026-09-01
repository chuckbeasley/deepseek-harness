namespace Harness.Sandbox;

/// <summary>
/// Service Definition for the same-world process-confinement capability seam (C# port of
/// <c>@deepseek-ai/hsh-sandbox</c> plus the policy VALUE surface of
/// <c>@deepseek-ai/hsh-sandbox-policy</c>): wrap exact subprocess argv under a host-path file
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

    /// <summary>
    /// Wrap exact subprocess argv under the policy (port of the TS
    /// <c>SandboxProvider.confine(argv, policy)</c>): the enforcing runner argv plus the run
    /// facts. <c>null</c> means the policy needs no confinement — the caller runs the argv as-is
    /// and reports no facts. A provider that cannot enforce a confining mode fails closed with
    /// <see cref="SandboxErrorCodes.Unavailable"/>: it never passes a confined call through
    /// unconfined.
    /// </summary>
    /// <param name="argv">the exact argv to wrap, without the executable resolution step.</param>
    /// <param name="policy">the resolved file-effect policy for this call.</param>
    /// <returns>the wrapped argv with the run facts, or <c>null</c> when unconfined.</returns>
    /// <exception cref="SandboxError">code <c>SANDBOX_UNAVAILABLE</c> when a confining mode has
    /// no usable backend on this host.</exception>
    ConfinedArgv? Confine(IReadOnlyList<string> argv, SandboxExecutionPolicy policy);
}

/// <summary>One confined execution: the runner argv plus the facts the runner will report.</summary>
public sealed record ConfinedArgv(
    /// <summary>The exact argv to spawn (runner plus the wrapped command).</summary>
    IReadOnlyList<string> Argv,
    /// <summary>The sandbox facts this execution runs under.</summary>
    ShellSandboxInfo Info);
