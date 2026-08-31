using System.Text.Json;

namespace Dsh.Sandbox.Tests;

/// <summary>The wire names, JSON wire shape, and error vocabulary of the sandbox value types.</summary>
public static class SandboxTypesTests
{
    public static void EveryModeHasItsExactWireName()
    {
        var expected = new (SandboxMode Mode, string Wire)[]
        {
            (SandboxMode.None, "none"),
            (SandboxMode.ReadOnly, "read-only"),
            (SandboxMode.WorkspaceWrite, "workspace-write"),
            (SandboxMode.DangerFullAccess, "danger-full-access"),
        };
        foreach (var (mode, wire) in expected)
        {
            Assert.Equal(wire, SandboxModes.WireName(mode), $"the wire name of {mode}");
            var json = JsonSerializer.SerializeToElement(mode);
            Assert.Equal(JsonValueKind.String, json.ValueKind, $"{mode} serializes as a JSON string");
            Assert.Equal(wire, json.GetString(), $"{mode} serializes to its wire name");
        }
    }

    public static void EnforcementWireNames()
    {
        Assert.Equal("full", JsonSerializer.SerializeToElement(SandboxEnforcement.Full).GetString(), "full enforcement wire name");
        Assert.Equal("partial", JsonSerializer.SerializeToElement(SandboxEnforcement.Partial).GetString(), "partial enforcement wire name");
    }

    public static void ShellSandboxInfoJsonRoundTripsThroughTheShellToolResult()
    {
        var info = new ShellSandboxInfo(SandboxMode.WorkspaceWrite, Denied: true, SandboxEnforcement.Partial, RunnerFailed: false);
        var json = JsonSerializer.SerializeToElement(info);
        Assert.Equal("workspace-write", json.GetProperty("mode").GetString(), "mode serializes to its wire name");
        Assert.Equal(true, json.GetProperty("denied").GetBoolean(), "denied carries through");
        Assert.Equal("partial", json.GetProperty("enforcement").GetString(), "enforcement serializes to its wire name");
        Assert.Equal(false, json.GetProperty("runnerFailed").GetBoolean(), "runnerFailed carries through");
        var back = JsonSerializer.Deserialize<ShellSandboxInfo>(json);
        Assert.NotNull(back, "the value deserializes");
        Assert.Equal(info, back!, "the round-trip preserves every field");

        var minimal = JsonSerializer.SerializeToElement(new ShellSandboxInfo(SandboxMode.None, Denied: false));
        Assert.Equal("none", minimal.GetProperty("mode").GetString(), "the minimal shape keeps mode");
        Assert.Equal(false, minimal.GetProperty("denied").GetBoolean(), "the minimal shape keeps denied");
        Assert.False(minimal.TryGetProperty("enforcement", out _), "absent enforcement is omitted (TS wire shape)");
        Assert.False(minimal.TryGetProperty("runnerFailed", out _), "absent runnerFailed is omitted (TS wire shape)");
    }

    public static void TheUnavailableErrorCarriesTheVerbatimFailClosedText()
    {
        var error = SandboxError.Unavailable(SandboxMode.ReadOnly);
        Assert.Equal(SandboxErrorCodes.Unavailable, error.Code, "the code is SANDBOX_UNAVAILABLE");
        Assert.True(
            error.Message.StartsWith("sandbox mode \"read-only\" is requested but no sandbox backend is usable on this host; refusing to run the command unconfined.", StringComparison.Ordinal),
            "the message names the mode and the refusal");
        Assert.True(error.Message.EndsWith("otherwise switch the consumer to danger-full-access.", StringComparison.Ordinal), "the message ends with the escape hatch");
        var withDetail = SandboxError.Unavailable(SandboxMode.WorkspaceWrite, "spawn failed");
        Assert.True(withDetail.Message.EndsWith(" Runner failure: spawn failed", StringComparison.Ordinal), "a runner detail is appended");
    }
}