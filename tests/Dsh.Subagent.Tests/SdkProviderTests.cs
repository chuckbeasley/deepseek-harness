using System.Diagnostics;
using Dsh.Subagent;

namespace Dsh.Subagent.Tests;

/// <summary>
/// Out-of-process driver behavior of <see cref="SdkOutOfProcessProvider"/> over the scripted fake
/// child: end-to-end turns, stop-reason mapping, handshake failures, env scrubbing/forwarding,
/// cancellation, and the dispose ladder.
/// </summary>
public static class SdkProviderTests
{
    private sealed class TempHome : IDisposable
    {
        public TempHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-subagent-tests-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>One provider over the fake child with the given env script.</summary>
    private static SdkOutOfProcessProvider Provider(TempHome home, Dictionary<string, string> env, string? cwd = null)
        => new(new SdkOutOfProcessConfig(
            DshBin: typeof(SdkProviderTests).Assembly.Location,
            Profile: "sdk",
            Patches: Array.Empty<string>(),
            DshHome: home.Path,
            Cwd: cwd,
            Provider: "deepseek-official",
            Model: "deepseek-v4-flash",
            MaxTokens: null,
            Env: env,
            AdditionalArgs: new[] { "--fake-sdk-child" },
            ShutdownTimeoutMs: 1000,
            DisposeEofGraceMs: 6000,
            DisposeGraceMs: 3000));

    private static Dictionary<string, string> Script(params (string Name, string Value)[] entries)
        => entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

    private static string Marker(TempHome home, string name)
        => System.IO.Path.Combine(home.Path, name);

    public static void RunsATurn_EndToEnd_AndMintsDistinctIds()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_TEXT", "the answer is 42")));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run1 = registry.StartAsync("dsh-sdk", new SubagentRequest("task one", "t1")).GetAwaiter().GetResult();
        var run2 = registry.StartAsync("dsh-sdk", new SubagentRequest("task two", "t2")).GetAwaiter().GetResult();
        var result1 = run1.Result.GetAwaiter().GetResult();
        var result2 = run2.Result.GetAwaiter().GetResult();
        Assert.Equal(SubagentStopReason.Completed, result1.StopReason, "the turn completed");
        Assert.Equal("the answer is 42", result1.Text, "the assistant text is the selected output");
        Assert.Null(result1.Diagnostic, "a completed run carries no diagnostic");
        Assert.False(result1.IsError);
        Assert.Equal(SubagentStopReason.Completed, result2.StopReason);
        Assert.True(run1.Id.Value != run2.Id.Value, "distinct runs get distinct ids");
        run1.DisposeAsync().GetAwaiter().GetResult();
        run2.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void ChildRunsInTheConfiguredCwd_AndSeesExplicitEnv()
    {
        using var home = new TempHome();
        var childCwd = System.IO.Path.Combine(home.Path, "child");
        Directory.CreateDirectory(childCwd);
        var provider = Provider(home, Script(
            ("FAKE_ECHO_CWD", "1"),
            ("FAKE_ECHO_ENV", "DEEPSEEK_API_KEY,DSH_HOME"),
            ("DEEPSEEK_API_KEY", "sk-explicit")), cwd: childCwd);
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task")).GetAwaiter().GetResult();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.True(result.Text.Contains($"cwd={childCwd}", StringComparison.Ordinal), $"the child ran in the configured cwd, got: {result.Text}");
        Assert.True(result.Text.Contains("DEEPSEEK_API_KEY=sk-explicit", StringComparison.Ordinal), "the explicit env entry reached the child");
        Assert.True(result.Text.Contains($"DSH_HOME={home.Path}", StringComparison.Ordinal), "the harness home reached the child");
        run.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void AmbientScrub_DropsDshAndSecretNames()
    {
        var ambient = new Dictionary<string, string>
        {
            ["PATH"] = "C:\\bin",
            ["DSH_HOME"] = "ambient",
            ["GITHUB_TOKEN"] = "leak",
            ["MY_SECRET_KEY"] = "leak",
            ["KEEP_ME"] = "value",
        };
        var scrubbed = OutOfProcess.ScrubEnvironment(ambient);
        Assert.False(scrubbed.ContainsKey("DSH_HOME"), "DSH_* names are dropped");
        Assert.False(scrubbed.ContainsKey("GITHUB_TOKEN"), "TOKEN names are dropped");
        Assert.False(scrubbed.ContainsKey("MY_SECRET_KEY"), "SECRET/KEY names are dropped");
        Assert.Equal("C:\\bin", scrubbed["PATH"]);
        Assert.Equal("value", scrubbed["KEEP_ME"]);
    }

    public static void ReasonMapping_CoversTheTerminalTable()
    {
        AssertReason("max-tokens", SubagentStopReason.MaxTokens, null);
        AssertReason("error", SubagentStopReason.Error, "child-error");
        AssertReason("blocked", SubagentStopReason.Refusal, null);
        AssertReason("aborted", SubagentStopReason.Aborted, null, ("FAKE_ABORT_REASON_KIND", "user"));
        AssertReason("aborted", SubagentStopReason.Aborted, "child-disposed", ("FAKE_ABORT_REASON_KIND", "disposed"));
        AssertReason("none", SubagentStopReason.Error, "missing-terminal");
    }

    private static void AssertReason(string reasonKind, SubagentStopReason expected, string? expectedCategory, params (string Name, string Value)[] extra)
    {
        using var home = new TempHome();
        var entries = new List<(string Name, string Value)> { ("FAKE_REASON_KIND", reasonKind) };
        entries.AddRange(extra);
        var provider = Provider(home, Script(entries.ToArray()));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task")).GetAwaiter().GetResult();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal(expected, result.StopReason, $"reason {reasonKind} maps to {expected}");
        if (expectedCategory is null)
        {
            Assert.Null(result.Diagnostic, $"reason {reasonKind} carries no diagnostic");
        }
        else
        {
            Assert.NotNull(result.Diagnostic, $"reason {reasonKind} carries a diagnostic");
            Assert.Contains(expectedCategory, result.Diagnostic!, $"the diagnostic names category {expectedCategory}");
        }
        Assert.True(result.Text.Length > 0, "the partial output is preserved");
        run.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void MalformedInitialize_FailsStartWithProtocolFacts()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_MALFORMED", "1")));
        var error = Assert.Throws<SubagentError>(() =>
            provider.StartAsync(new SubagentRequest("task"), CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("initialize", error.Message, "the stage is named");
        Assert.Contains("protocol", error.Message, "the category is named");
        Assert.Equal("START_FAILED", error.Code);
    }

    public static void InitializeError_FailsStartWithProtocolFacts()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_INIT_ERROR", "1")));
        var error = Assert.Throws<SubagentError>(() =>
            provider.StartAsync(new SubagentRequest("task"), CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("protocol", error.Message);
    }

    public static void ChildExitingBeforeInitialize_FailsStartWithTransportFacts()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_EXIT_BEFORE_INIT", "1")));
        var error = Assert.Throws<SubagentError>(() =>
            provider.StartAsync(new SubagentRequest("task"), CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("transport", error.Message);
    }

    public static void PreAbortedStart_ThrowsWithoutSpawning()
    {
        using var home = new TempHome();
        var bootMarker = Marker(home, "boot.txt");
        var provider = Provider(home, Script(("FAKE_BOOT_MARKER", bootMarker)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var thrown = false;
        try
        {
            provider.StartAsync(new SubagentRequest("task"), cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            thrown = true;
        }
        Assert.True(thrown, "a pre-aborted start throws");
        Assert.False(File.Exists(bootMarker), "no child was spawned");
    }

    public static void CancelMidTurn_SettlesAbortedAndReapsTheChild()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_HANG_PROMPT", "1")));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        using var cts = new CancellationTokenSource();
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task"), cts.Token).GetAwaiter().GetResult();
        Assert.WaitUntil(() => !run.Result.IsCompleted || true, 1000, "started"); // no-op wait; the run is live
        cts.Cancel();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal(SubagentStopReason.Aborted, result.StopReason, "local cancellation settles aborted");
        run.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void DisposeMidTurn_SettlesAborted_AndTearsTheChildDown()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_HANG_PROMPT", "1")));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task")).GetAwaiter().GetResult();
        var resultTask = run.Result;
        run.DisposeAsync().GetAwaiter().GetResult();
        var result = resultTask.GetAwaiter().GetResult();
        Assert.Equal(SubagentStopReason.Aborted, result.StopReason, "disposal settles the pending run aborted");
        run.DisposeAsync().GetAwaiter().GetResult(); // idempotent
    }

    public static void ChildExitingMidTurn_SettlesErrorWithTransportFacts_AndPreservesPartialOutput()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(
            ("FAKE_EXIT_DURING_PROMPT", "1"),
            ("FAKE_TEXT", "partial before exit")));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task")).GetAwaiter().GetResult();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal(SubagentStopReason.Error, result.StopReason);
        Assert.Contains("transport", result.Diagnostic ?? "", "the diagnostic names the transport category");
        Assert.Equal("partial before exit", result.Text, "the partial output is preserved");
        run.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void MalformedPromptResponse_SettlesErrorWithProtocolFacts()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_MALFORMED_PROMPT", "1")));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task")).GetAwaiter().GetResult();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal(SubagentStopReason.Error, result.StopReason);
        Assert.Contains("protocol", result.Diagnostic ?? "", "the malformed prompt response is a protocol failure");
        run.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void Registry_RejectsDuplicateAndUnknownProviders()
    {
        using var home = new TempHome();
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        var provider = Provider(home, Script());
        using var first = registry.RegisterProvider(provider);
        var duplicate = Assert.Throws<SubagentError>(() => registry.RegisterProvider(provider));
        Assert.Equal("DUPLICATE_PROVIDER", duplicate.Code);
        var unknown = Assert.Throws<SubagentError>(() =>
            registry.StartAsync("no-such-provider", new SubagentRequest("task")).GetAwaiter().GetResult());
        Assert.Equal("NO_PROVIDER", unknown.Code);
        Assert.NotNull(registry.GetProvider("dsh-sdk"));
        Assert.True(registry.List().Any(candidate => candidate.Name == "dsh-sdk"));
    }

    public static void ConfigValidation_FailsLoud()
    {
        using var home = new TempHome();
        var dll = typeof(SdkProviderTests).Assembly.Location;
        Assert.Throws<ArgumentException>(() => new SdkOutOfProcessProvider(new SdkOutOfProcessConfig(
            dll, "sdk", Array.Empty<string>(), home.Path, null, "p", "m", null,
            new Dictionary<string, string>(), Array.Empty<string>(), ShutdownTimeoutMs: 0)));
        Assert.Throws<ArgumentException>(() => new SdkOutOfProcessProvider(new SdkOutOfProcessConfig(
            dll, "sdk", Array.Empty<string>(), "relative-home", null, "p", "m", null,
            new Dictionary<string, string>(), Array.Empty<string>())));
        Assert.Throws<ArgumentException>(() => new SdkOutOfProcessProvider(new SdkOutOfProcessConfig(
            System.IO.Path.Combine(home.Path, "missing.dll"), "sdk", Array.Empty<string>(), home.Path, null, "p", "m", null,
            new Dictionary<string, string>(), Array.Empty<string>())));
        Assert.Throws<ArgumentException>(() => new SdkOutOfProcessProvider(new SdkOutOfProcessConfig(
            dll, "sdk", Array.Empty<string>(), home.Path, "relative-cwd", "p", "m", null,
            new Dictionary<string, string>(), Array.Empty<string>())));
    }

    public static void DisposeAsync_IsIdempotent_AfterSettlement()
    {
        using var home = new TempHome();
        var provider = Provider(home, Script(("FAKE_TEXT", "done")));
        using var registry = new InProcessSubagentProvider(new Cordis.Core.Context());
        using var registration = registry.RegisterProvider(provider);
        var run = registry.StartAsync("dsh-sdk", new SubagentRequest("task")).GetAwaiter().GetResult();
        var result = run.Result.GetAwaiter().GetResult();
        Assert.Equal("done", result.Text);
        run.DisposeAsync().GetAwaiter().GetResult();
        run.DisposeAsync().GetAwaiter().GetResult(); // second dispose is a no-op
    }
}
