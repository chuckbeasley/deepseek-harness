namespace Harness.Sandbox.Tests;

/// <summary>The escalation ladder, argument-pairing validation, model-facing markers, and root derivation.</summary>
public static class EscalationTests
{
    public static void TheStrictlyWiderLadder()
    {
        Assert.Equal(
            new[] { SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess },
            SandboxEscalation.WiderModes[SandboxMode.ReadOnly],
            "read-only escalates to either wider mode");
        Assert.Equal(
            new[] { SandboxMode.DangerFullAccess },
            SandboxEscalation.WiderModes[SandboxMode.WorkspaceWrite],
            "workspace-write escalates only to full access");
        Assert.False(SandboxEscalation.WiderModes.ContainsKey(SandboxMode.DangerFullAccess), "danger-full-access has no wider mode");
        Assert.Equal(
            new[] { SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess },
            SandboxEscalation.EscalationTargets,
            "the target enum is the closed set (read-only is the floor)");
    }

    public static void EscalationArgPairingValidation()
    {
        SandboxEscalation.ValidateEscalationArgs(null, null);
        SandboxEscalation.ValidateEscalationArgs("workspace-write", "because the workspace needs it");

        var missingJustification = Assert.Throws<ArgumentException>(() => SandboxEscalation.ValidateEscalationArgs("workspace-write", null));
        Assert.Equal("invalid escalation: sandbox_permissions requires a justification", missingJustification.Message);

        var orphanReason = Assert.Throws<ArgumentException>(() => SandboxEscalation.ValidateEscalationArgs(null, "orphan reason"));
        Assert.Equal("invalid escalation: justification is only valid together with sandbox_permissions", orphanReason.Message);

        var blank = Assert.Throws<ArgumentException>(() => SandboxEscalation.ValidateEscalationArgs("workspace-write", "   "));
        Assert.Equal("invalid justification: expected a non-empty sentence", blank.Message);
    }

    public static void TheModelFacingMarkers()
    {
        Assert.Equal("[sandbox: file access denied under read-only mode]", SandboxEscalation.SandboxDenialMarker(SandboxMode.ReadOnly));
        Assert.Equal("[sandbox: file access denied under workspace-write mode]", SandboxEscalation.SandboxDenialMarker(SandboxMode.WorkspaceWrite));
        Assert.Equal("[sandbox: file access denied under danger-full-access mode]", SandboxEscalation.SandboxDenialMarker(SandboxMode.DangerFullAccess));
        Assert.True(
            SandboxEscalation.EscalationHintMarker("command").Contains("retry this exact command once with sandbox_permissions", StringComparison.Ordinal),
            "the hint names the command subject");
        Assert.True(
            SandboxEscalation.EscalationHintMarker("operation").Contains("retry this exact operation once with sandbox_permissions", StringComparison.Ordinal),
            "the hint names the operation subject");
    }

    public static void CanonicalPathAndWritableRoots()
    {
        var canonical = SandboxRoots.CanonicalPath(".");
        Assert.True(Path.IsPathRooted(canonical), "a resolvable path canonicalizes to an absolute path");

        Assert.Empty(SandboxRoots.WritableRoots(new SandboxExecutionPolicy(SandboxMode.ReadOnly, "C:\\unused")), "read-only allows nothing");

        var root = Path.Combine(Path.GetTempPath(), "hsh-sandbox-roots-" + Guid.NewGuid().ToString("N"));
        var roots = SandboxRoots.WritableRoots(new SandboxExecutionPolicy(SandboxMode.WorkspaceWrite, root));
        Assert.Equal(3, roots.Count, "workspace-write allows the workspace root, /tmp, and the platform temp dir");
        Assert.True(roots.Contains(Path.GetFullPath(root)), "the workspace root is writable");
    }
}