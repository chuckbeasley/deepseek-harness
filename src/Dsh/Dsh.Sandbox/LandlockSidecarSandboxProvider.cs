using System.Diagnostics;
using Harness.Cordis.Core;

namespace Harness.Sandbox;

/// <summary>Configuration for the Landlock sidecar backend.</summary>
public sealed record LandlockSidecarConfig(
    /// <summary>Explicit sidecar path; omission resolves <c>landlock-run</c> on PATH.</summary>
    string? SidecarPath = null,
    /// <summary>The fallback workspace root for <c>workspace-write</c> policies.</summary>
    string? WorkspaceRoot = null);

/// <summary>One cached probe of the sidecar.</summary>
internal sealed record SidecarProbe(bool Usable, SandboxEnforcement? Enforcement, string? Detail);

/// <summary>
/// The Landlock sidecar provider (ctx.sandbox; the native-bridge backend of the seam): wraps
/// exact argv into <c>landlock-run</c> runner argv per the documented sidecar contract
/// (native/landlock-run): <c>--ro</c>/<c>--rw</c> grants, the mandatory <c>--</c> separator, and
/// <c>--probe</c> reporting <c>landlock: fully enforced</c> or <c>landlock: partially enforced
/// (older ABI)</c>. The provider fails closed: a confining mode with no usable sidecar never
/// passes the call through unconfined.
/// </summary>
public sealed class LandlockSidecarSandboxProvider : Service, ISandboxService
{
    private readonly object _gate = new();
    private readonly string? _sidecarPath;
    private readonly string _workspaceRoot;
    private SidecarProbe? _probe;

    /// <summary>Create the provider and register it as <c>sandbox</c>.</summary>
    public LandlockSidecarSandboxProvider(Context ctx, LandlockSidecarConfig? config = null)
        : base(ctx, "sandbox")
    {
        _sidecarPath = config?.SidecarPath ?? ResolveOnPath("landlock-run");
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
            request.Mode ?? DefaultMode,
            request.WorkspaceRoot is string root ? SandboxRoots.CanonicalPath(root) : _workspaceRoot);
    }

    /// <inheritdoc />
    public ShellSandboxInfo DescribeRun(SandboxExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var probe = Probe();
        if (!probe.Usable && policy.Mode is SandboxMode.ReadOnly or SandboxMode.WorkspaceWrite)
        {
            throw SandboxError.Unavailable(policy.Mode, probe.Detail);
        }
        return new ShellSandboxInfo(policy.Mode, Denied: false, Enforcement: probe.Enforcement);
    }

    /// <inheritdoc />
    public ConfinedArgv? Confine(IReadOnlyList<string> argv, SandboxExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Mode is not (SandboxMode.ReadOnly or SandboxMode.WorkspaceWrite))
        {
            return null; // unconfined modes run as-is
        }
        var probe = Probe();
        if (!probe.Usable)
        {
            // Fail closed: never pass a confined call through unconfined.
            throw SandboxError.Unavailable(policy.Mode, probe.Detail);
        }
        var runner = new List<string>(SidecarArgv());
        if (policy.Mode == SandboxMode.WorkspaceWrite)
        {
            foreach (var root in SandboxRoots.WritableRoots(policy))
            {
                runner.Add("--rw");
                runner.Add(root);
            }
        }
        runner.Add("--");
        runner.AddRange(argv);
        return new ConfinedArgv(runner, new ShellSandboxInfo(policy.Mode, Denied: false, Enforcement: probe.Enforcement));
    }

    /// <summary>The spawn shape of the sidecar: a managed .dll runs under <c>dotnet</c>.</summary>
    private IReadOnlyList<string> SidecarArgv()
        => _sidecarPath is null
            ? throw SandboxError.Unavailable(SandboxMode.ReadOnly, "the landlock-run sidecar is not installed")
            : _sidecarPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? new[] { "dotnet", _sidecarPath }
                : new[] { _sidecarPath };

    /// <summary>Probe the sidecar once and cache the result.</summary>
    private SidecarProbe Probe()
    {
        lock (_gate)
        {
            if (_probe is not null) return _probe;
            _probe = RunProbe();
            return _probe;
        }
    }

    private SidecarProbe RunProbe()
    {
        if (_sidecarPath is null)
        {
            return new SidecarProbe(Usable: false, Enforcement: null, Detail: "the landlock-run sidecar is not installed");
        }
        try
        {
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var argv = SidecarArgv();
            info.FileName = argv[0];
            foreach (var argument in argv.Skip(1)) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("--probe");
            using var process = Process.Start(info);
            if (process is null)
            {
                return new SidecarProbe(Usable: false, Enforcement: null, Detail: "the sidecar did not start");
            }
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return new SidecarProbe(Usable: false, Enforcement: null, Detail: $"the sidecar probe exited {process.ExitCode}");
            }
            if (stdout.Contains("fully enforced", StringComparison.Ordinal))
            {
                return new SidecarProbe(Usable: true, Enforcement: SandboxEnforcement.Full, Detail: null);
            }
            if (stdout.Contains("partially enforced", StringComparison.Ordinal))
            {
                return new SidecarProbe(Usable: true, Enforcement: SandboxEnforcement.Partial, Detail: "older Landlock ABI");
            }
            return new SidecarProbe(Usable: false, Enforcement: null, Detail: "the sidecar probe answered unexpectedly");
        }
        catch (Exception error)
        {
            return new SidecarProbe(Usable: false, Enforcement: null, Detail: error.Message);
        }
    }

    private static string? ResolveOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // an unreadable PATH entry contributes nothing
            }
        }
        return null;
    }
}
