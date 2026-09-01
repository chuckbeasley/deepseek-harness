using System.Diagnostics;
using System.Text.Json;

namespace Harness.Lsp.Tests;

/// <summary>The serialized, abortable instance lifecycle over a spawned fixture (mirrors instance.spec.ts).</summary>
public static class InstanceTests
{
    private static LspInstanceSpec InstanceSpec(
        string ws,
        Dictionary<string, string> env,
        int shutdownTimeoutMs = 200,
        int killGraceMs = 200)
        => new(
            LspTestHarness.FixtureCommand,
            LspTestHarness.FixtureArgs,
            ws,
            env,
            16_000_000,
            100_000,
            killGraceMs,
            JsonSerializer.SerializeToElement(new { setting = 42 }),
            LspTestHarness.WorkspaceUri(ws),
            JsonSerializer.SerializeToElement(new { init = true }),
            shutdownTimeoutMs);

    /// <summary>Run one query the way the provider would: source read first, then the instance query.</summary>
    private static Task<LspQueryResult> Run(LspInstance instance, string ws, LspOperation operation, CancellationToken ct = default)
    {
        var file = Path.Combine(ws, "a.ts");
        var source = new HostSource(new Uri(file).AbsoluteUri, File.ReadAllText(file));
        var request = new LspProviderQuery(operation, "a.ts", new LspPosition(0, 6), ws, "typescript");
        return instance.QueryAsync(request, source, ct);
    }

    /// <summary>Write normally except for one method whose callback receives a deterministic transport error.</summary>
    private static LspConnectionWriter FailingWriter(string method)
    {
        return (stdin, message, done) =>
        {
            if (message.Method == method)
            {
                done(new InvalidOperationException($"fixture {method} failure"));
                return;
            }
            LspConnection.DefaultWriter(stdin, message, done);
        };
    }

    private static async Task DisposeAsync(LspInstance instance)
        => await instance.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(20));

    public static async Task AnswersWorkspaceConfigurationPerItem()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "configuration"), ("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                var result = await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result is LspLocationsResult locations && locations.Locations.Count == 0, "a healthy configuration answer keeps the query working");
                Assert.Equal(LspTestHarness.WorkspaceUri(ws), ((LspLocationsResult)result).ResolvedWorkspaceUri, "the resolved workspace uri is the canonical one");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task AcceptsLifecycleRegisterCapability()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "lifecycle"), ("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                var result = await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result is LspLocationsResult, "the lifecycle request is acknowledged with an empty result");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task RejectsApplyEditButKeepsServing()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "applyEdit"), ("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                var result = await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result is LspLocationsResult, "applyEdit is refused but the query still resolves");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task RejectsUnknownServerRequestButKeepsServing()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_ON_OPEN", "unknown"), ("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                var result = await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result is LspLocationsResult, "an unknown server request is refused but the query still resolves");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task References_SendsIncludeDeclaration()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var uri = new Uri(Path.Combine(ws, "a.ts")).AbsoluteUri;
            var refs = JsonSerializer.Serialize(new[]
            {
                new { uri, range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 3 } } },
            });
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_REFS", refs))), LspConnection.DefaultSpawner);
            try
            {
                var result = await Run(instance, ws, LspOperation.FindReferences).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result is LspLocationsResult locations && locations.Locations.Count == 1, "the references result resolves");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task PreAbortedQuery_Rejects()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                LspAbort.SetReason(cts, new InvalidOperationException("pre-abort"));
                cts.Cancel();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(instance, ws, LspOperation.GoToDefinition, cts.Token));
                Assert.Contains("pre-abort", error.Message, "the abort reason surfaces");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task CancelsInFlightRequestOnAbort_AndRejects()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        var openMarker = Path.Combine(root, "open.log");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_HANG", "1"), ("LSP_FAKE_OPEN_MARKER", openMarker))), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                LspAbort.SetReason(cts, new InvalidOperationException("mid-flight"));
                var pending = Run(instance, ws, LspOperation.GoToDefinition, cts.Token);
                // The open marker proves didOpen reached the server; the hanging request is now in flight.
                await LspTestHarness.WaitForFileAsync(openMarker);
                cts.Cancel();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("mid-flight", error.Message, "the in-flight abort reason surfaces");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task TerminatesInstance_WhenServerIgnoresCancelPastGrace()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        var openMarker = Path.Combine(root, "open.log");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_HANG", "1"), ("LSP_FAKE_OPEN_MARKER", openMarker)), killGraceMs: 100), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                LspAbort.SetReason(cts, new InvalidOperationException("mid-flight"));
                var pending = Run(instance, ws, LspOperation.GoToDefinition, cts.Token);
                await LspTestHarness.WaitForFileAsync(openMarker);
                cts.Cancel();
                await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.True(instance.Dead, "ignoring cancellation past the grace tears the instance down");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task ResolvesCancelGrace_WhenServerHonorsCancelRequest()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        var openMarker = Path.Combine(root, "open.log");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_HANG", "1"), ("LSP_FAKE_HONOR_CANCEL", "1"), ("LSP_FAKE_OPEN_MARKER", openMarker)), killGraceMs: 2_000), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                LspAbort.SetReason(cts, new InvalidOperationException("mid-flight"));
                var pending = Run(instance, ws, LspOperation.GoToDefinition, cts.Token);
                await LspTestHarness.WaitForFileAsync(openMarker);
                cts.Cancel();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("mid-flight", error.Message, "the abort reason surfaces");
                Assert.True(!instance.Dead, "the server honored cancellation within grace, so the instance survives");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task ObservesAbort_DuringSlowInitializeHandshake()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_HANG_INITIALIZE", "1")), killGraceMs: 100), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                LspAbort.SetReason(cts, new InvalidOperationException("handshake-abort"));
                var pending = Run(instance, ws, LspOperation.GoToDefinition, cts.Token);
                await Task.Delay(200);
                cts.Cancel();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("handshake-abort", error.Message, "the abort is observed during the handshake wait");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Terminates_WhenAbortInterruptsBackpressuredDidOpenWrite()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        File.WriteAllText(Path.Combine(ws, "a.ts"), new string('x', 2_000_000));
        var marker = Path.Combine(root, "initialized.log");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_INITIALIZED_MARKER", marker), ("LSP_FAKE_PAUSE_STDIN_AFTER_INITIALIZED", "1")), shutdownTimeoutMs: 100, killGraceMs: 100), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                LspAbort.SetReason(cts, new InvalidOperationException("didOpen-abort"));
                var pending = Run(instance, ws, LspOperation.GoToDefinition, cts.Token);
                await LspTestHarness.WaitForFileAsync(marker);
                // Let the client enter the large didOpen write after the fixture has paused stdin.
                await Task.Delay(100);
                cts.Cancel();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("didOpen-abort", error.Message, "the abort surfaces from the backpressured write");
                Assert.True(instance.Dead, "the backpressured write forces teardown");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Terminates_WhenStdinFailsDuringDidOpenWrite()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(), shutdownTimeoutMs: 100, killGraceMs: 100), LspConnection.DefaultSpawner, FailingWriter("textDocument/didOpen"));
            try
            {
                await Assert.ThrowsAsync<Exception>(() => Run(instance, ws, LspOperation.GoToDefinition));
                Assert.True(instance.Dead, "a didOpen write failure invalidates the instance");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task AwaitsProcessExit_BeforeRejectingRequestWriteFailure()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(), shutdownTimeoutMs: 100, killGraceMs: 100), LspConnection.DefaultSpawner, FailingWriter("textDocument/definition"));
            var pid = instance.Connection.Pid;
            try
            {
                var error = await Assert.ThrowsAsync<Exception>(() => Run(instance, ws, LspOperation.GoToDefinition));
                Assert.Contains("fixture textDocument/definition failure", error.Message, "the injected write failure rejects the query");
                Assert.True(!LspTestHarness.ProcessAlive(pid), "the owned subprocess reached quiescence before the rejection");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Rejects_WhenServerLacksOperationCapability()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_CAPS", "{\"definitionProvider\":false}"), ("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                var error = await Assert.ThrowsAsync<LspError>(() => Run(instance, ws, LspOperation.GoToDefinition));
                Assert.Contains("does not support goToDefinition", error.Message, "the capability rejection names the operation");
                Assert.Equal("LSP_UNSUPPORTED_OPERATION", error.Code, "the code is LSP_UNSUPPORTED_OPERATION");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task PropagatesServerErrorResponse_EvenWithLiveSignal()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_ERROR", "1"))), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                var error = await Assert.ThrowsAsync<Exception>(() => Run(instance, ws, LspOperation.GoToDefinition, cts.Token));
                Assert.Contains("server refused", error.Message, "a server error response with a live signal is not an abort");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task KeepsSettledResult_ButTearsDown_WhenDidCloseCannotBeWritten()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_DEF", "null")), shutdownTimeoutMs: 100, killGraceMs: 100), LspConnection.DefaultSpawner, FailingWriter("textDocument/didClose"));
            try
            {
                var result = await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result is LspLocationsResult locations && locations.Locations.Count == 0, "the settled result is preserved");
                Assert.True(instance.Dead, "the didClose write failure invalidates the instance");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Dispose_LetsServerFinishProtocolExit()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        var marker = Path.Combine(root, "graceful-exit.log");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_DEF", "null"), ("LSP_FAKE_EXIT_DELAY_MS", "75"), ("LSP_FAKE_EXIT_MARKER", marker)), shutdownTimeoutMs: 500), LspConnection.DefaultSpawner);
            try
            {
                await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                await DisposeAsync(instance);
                await LspTestHarness.WaitForFileAsync(marker);
                Assert.Equal("EXIT\nCLEAN\n", File.ReadAllText(marker), "the server completes protocol exit before escalation");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Dispose_IsIdempotent()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                await DisposeAsync(instance);
                await DisposeAsync(instance); // must not throw
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Query_AfterDispose_RejectsLspDisposed()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                await DisposeAsync(instance);
                var error = await Assert.ThrowsAsync<LspError>(() => Run(instance, ws, LspOperation.GoToDefinition));
                Assert.Equal("LSP_DISPOSED", error.Code, "a query after disposal rejects with LSP_DISPOSED");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Dead_AfterProcessCloses()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_DEF", "null"))), LspConnection.DefaultSpawner);
            try
            {
                await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                await DisposeAsync(instance);
                Assert.True(instance.Dead, "the instance reports dead after the process closes");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Dispose_EscalatesToKill_WhenServerIgnoresShutdownAndSigterm()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_NO_SHUTDOWN", "1"), ("LSP_FAKE_TRAP_SIGTERM", "1")), shutdownTimeoutMs: 100, killGraceMs: 100), LspConnection.DefaultSpawner);
            try
            {
                await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                await DisposeAsync(instance); // escalates to a tree kill and resolves
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task Dispose_AwaitsSurvivingProcessTreeHelper()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        var marker = Path.Combine(root, "helper.pid");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_SPAWN_HELPER", marker)), shutdownTimeoutMs: 100, killGraceMs: 100), LspConnection.DefaultSpawner);
            try
            {
                await Run(instance, ws, LspOperation.GoToDefinition).WaitAsync(TimeSpan.FromSeconds(30));
                await LspTestHarness.WaitForFileAsync(marker);
                var helperPid = int.Parse(File.ReadAllText(marker));
                try
                {
                    var first = instance.DisposeAsync();
                    await instance.DisposeAsync();
                    Assert.True(!LspTestHarness.ProcessAlive(helperPid), "the surviving helper is killed with the tree");
                    await first;
                }
                finally
                {
                    if (LspTestHarness.ProcessAlive(helperPid))
                    {
                        try
                        {
                            Process.GetProcessById(helperPid).Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // Already gone.
                        }
                    }
                    await LspTestHarness.WaitForProcessExit(helperPid);
                }
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task NonErrorAbortReason_BecomesGenericAbortedError()
    {
        var root = LspTestHarness.CreateWorkspace();
        var ws = LspTestHarness.WorkspacePath(root);
        var openMarker = Path.Combine(root, "open.log");
        try
        {
            var instance = new LspInstance(InstanceSpec(ws, LspTestHarness.Env(("LSP_FAKE_HANG", "1"), ("LSP_FAKE_OPEN_MARKER", openMarker))), LspConnection.DefaultSpawner);
            try
            {
                using var cts = new CancellationTokenSource();
                var pending = Run(instance, ws, LspOperation.GoToDefinition, cts.Token);
                await LspTestHarness.WaitForFileAsync(openMarker);
                cts.Cancel();
                var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                Assert.Contains("aborted", error.Message, "a reason-less abort classifies as the generic aborted error");
            }
            finally
            {
                await DisposeAsync(instance);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
