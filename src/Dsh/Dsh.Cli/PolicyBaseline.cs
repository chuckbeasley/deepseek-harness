using Harness.Cordis.Core;
using Harness.Cordis.Plugin.Loader;
using Harness.Interaction;
using Harness.Sandbox;

namespace Harness.Cli;

/// <summary>
/// The session policy baseline: every session's log opens with its effective permission preset,
/// sandbox mode, and approval policy (the TS base composition's baseline events). The snapshot
/// overlay mounts the row with the recorded values; the product bundles adopt it at cutover.
/// </summary>
public static class PolicyBaseline
{
    /// <summary>The default permission preset id.</summary>
    public const string DefaultPreset = "danger-full-access";

    /// <summary>The default sandbox mode.</summary>
    public const string DefaultSandboxMode = "danger-full-access";

    /// <summary>The default approval policy.</summary>
    public const string DefaultApprovalPolicy = "never";

    /// <summary>Model-facing statement for one sandbox mode (the TS sandbox-policy sentences).</summary>
    public static string SandboxPolicyText(string mode, string workspaceRoot)
        => mode switch
        {
            "read-only" => "Current DSH file policy: read-only. Any available operation enforced by the DSH file sandbox cannot modify files in the standing mode. Do not refuse a required modification from this policy alone: try an available tool normally and follow any denial and escalation guidance it returns.",
            "workspace-write" => $"Current DSH file policy: workspace-write. Any available operation enforced by the DSH file sandbox may modify files under the session workspace: {System.Text.Json.JsonSerializer.Serialize(workspaceRoot)}. Some platform temporary areas may also be writable.",
            "danger-full-access" => "Current DSH file policy: danger-full-access. The DSH file sandbox does not restrict file modifications by available operations.",
            _ => throw new InvalidOperationException($"unknown sandbox mode {System.Text.Json.JsonSerializer.Serialize(mode)}"),
        };

    /// <summary>Model-facing statement for one approval policy (the TS user-approval sentences).</summary>
    public static string ApprovalPolicyText(string policy)
        => policy switch
        {
            "never" => "Approval prompts are disabled in this session: actions that require approval are rejected automatically \u2014 do not request sandbox escalation (do not set `sandbox_permissions`).",
            "ask" => "Approval policy: ask. Operations that require approval may ask through the configured answerers; without an available answerer, the request fails closed.",
            _ => throw new InvalidOperationException($"unknown approval policy {System.Text.Json.JsonSerializer.Serialize(policy)}"),
        };
}

/// <summary>
/// Spine row "policyBaseline": appends the permission/sandbox/approval baseline events to every
/// session at creation, in the recorded order. Config: preset, sandboxMode, approvalPolicy.
/// </summary>
public sealed class PolicyBaselinePlugin : ILoaderPlugin
{
    /// <inheritdoc />
    public ValueTask<IDisposable?> ApplyAsync(Harness.Cordis.Core.Context ctx, object? config)
    {
        // The permission preset comes from $DSH_PERMISSION_MODE when set (the TS headless
        // contract); the sandbox mode and approval policy derive from the preset exactly like the
        // TS presets table (danger-full-access disables approval; the other presets ask). The
        // config values only fall back for a preset outside the three known modes.
        var preset = Environment.GetEnvironmentVariable("DSH_PERMISSION_MODE") is { Length: > 0 } envPreset
            ? envPreset
            : SpineRegistry.ConfigString(config, "preset") ?? PolicyBaseline.DefaultPreset;
        var mode = preset is "workspace-write" or "read-only" or "danger-full-access"
            ? preset
            : SpineRegistry.ConfigString(config, "sandboxMode") ?? PolicyBaseline.DefaultSandboxMode;
        var policy = preset == "danger-full-access" ? "never" : "ask";
        Harness.Interaction.InteractionEventTypes.Register();
        Harness.Sandbox.SandboxEventTypes.Register();
        var subscription = ctx.On("session/created", (Delegate)(Action<Harness.Session.Session>)(session =>
        {
            session.Append(new PermissionPresetEvent { Preset = preset });
            session.Append(new SandboxModeEvent { Mode = ParseSandboxMode(mode) });
            session.Append(new ApprovalPolicyEvent { Policy = ParseApprovalPolicy(policy) });
        }));
        return ValueTask.FromResult<IDisposable?>(subscription);
    }

    private static SandboxMode ParseSandboxMode(string mode) => mode switch
    {
        "read-only" => SandboxMode.ReadOnly,
        "workspace-write" => SandboxMode.WorkspaceWrite,
        "danger-full-access" => SandboxMode.DangerFullAccess,
        _ => throw new InvalidOperationException($"unknown sandbox mode {System.Text.Json.JsonSerializer.Serialize(mode)}"),
    };

    private static ApprovalPolicy ParseApprovalPolicy(string policy) => policy switch
    {
        "never" => ApprovalPolicy.Never,
        "ask" => ApprovalPolicy.Ask,
        _ => throw new InvalidOperationException($"unknown approval policy {System.Text.Json.JsonSerializer.Serialize(policy)}"),
    };
}

/// <summary>
/// Spine row "policyContext": registers the sandbox and approval runtime-context providers on the
/// agent loop, so each pre-step projects the policy snapshot message (the TS sandbox-policy and
/// user-approval context contributions).
/// </summary>
public sealed class PolicyContextPlugin : ILoaderPlugin
{
    /// <inheritdoc />
    public ValueTask<IDisposable?> ApplyAsync(Harness.Cordis.Core.Context ctx, object? config)
    {
        var loop = ctx.Get<Harness.AgentLoop.AgentLoop>("agentLoop")
            ?? throw new InvalidOperationException("policyContext requires the \"agentLoop\" row");
        var preset = Environment.GetEnvironmentVariable("DSH_PERMISSION_MODE") is { Length: > 0 } envPreset
            ? envPreset
            : SpineRegistry.ConfigString(config, "preset") ?? PolicyBaseline.DefaultPreset;
        var mode = preset is "workspace-write" or "read-only" or "danger-full-access"
            ? preset
            : SpineRegistry.ConfigString(config, "sandboxMode") ?? PolicyBaseline.DefaultSandboxMode;
        var policy = preset == "danger-full-access" ? "never" : "ask";
        var workspaceRoot = SpineRegistry.ConfigString(config, "workspaceRoot") ?? Environment.CurrentDirectory;
        var sandboxText = PolicyBaseline.SandboxPolicyText(mode, workspaceRoot);
        var approvalText = PolicyBaseline.ApprovalPolicyText(policy);
        var sandboxProvider = loop.RegisterContextProvider(() => Task.FromResult(
            new Harness.AgentLoop.RuntimeContextPart(sandboxText, new[] { new Harness.Llm.NamedSection("sandbox:policy", sandboxText) })));
        var approvalProvider = loop.RegisterContextProvider(() => Task.FromResult(
            new Harness.AgentLoop.RuntimeContextPart(approvalText, new[] { new Harness.Llm.NamedSection("approval:policy", approvalText) })));
        return ValueTask.FromResult<IDisposable?>(new SpineDisposables(sandboxProvider, approvalProvider));
    }
}