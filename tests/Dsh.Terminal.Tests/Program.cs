namespace Dsh.Terminal.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Suites = new (string, Func<Task>)[]
    {
        ("open sends and reads output", TerminalTests.Open_SendsAndReadsOutput),
        ("sends retain scrollback across operations", TerminalTests.Send_WithoutSubmit_AppendsNoNewline),
        ("read returns the retained scrollback", TerminalTests.Read_ReturnsTheRetainedScrollback),
        ("unknown backend type fails loud", TerminalTests.UnknownBackendType_FailsLoud),
        ("dispose closes live sessions", TerminalTests.Dispose_ClosesLiveSessions),
        ("sanitizer strips CSI sequences", () => { SanitizerTests.StripsCsiSequences(); return Task.CompletedTask; }),
        ("sanitizer strips OSC sequences and ignores unrelated markers", () => { SanitizerTests.StripsOscSequences_AndDetectsThePromptMarker(); return Task.CompletedTask; }),
        ("sanitizer detects the prompt marker with BEL", () => { SanitizerTests.PromptMarker_WithBelTerminator_IsDetected(); return Task.CompletedTask; }),
        ("sanitizer detects the prompt marker with ESC-ST", () => { SanitizerTests.PromptMarker_WithEscStTerminator_IsDetected(); return Task.CompletedTask; }),
        ("sanitizer carries split markers across chunks", () => { SanitizerTests.PromptMarker_SplitAcrossChunks_CarriesState(); return Task.CompletedTask; }),
        ("sanitizer normalizes CRLF and removes BEL", () => { SanitizerTests.NormalizesCrLfAndRemovesBel(); return Task.CompletedTask; }),
        ("sanitizer drops short escapes", () => { SanitizerTests.DropsShortEscapes(); return Task.CompletedTask; }),
        ("sanitizer reset clears marker state", () => { SanitizerTests.ResetClearsMarkerState(); return Task.CompletedTask; }),
    };

    /// <summary>The ConPTY suites run only on Windows (the backend is Windows-only).</summary>
    private static readonly (string Name, Func<Task> Run)[] ConPtySuites = new (string, Func<Task>)[]
    {
        ("conpty: open sends and reads output with prompt-marker readiness", ConPtyTests.Open_SendsAndReadsOutput),
        ("conpty: submit writes carriage return byte-exact", ConPtyTests.Submit_WritesCarriageReturn_ByteExact),
        ("conpty: read returns the retained scrollback", ConPtyTests.Read_ReturnsTheRetainedScrollback),
        ("conpty: session exit settles the active send", ConPtyTests.SessionExit_SettlesTheActiveSend),
        ("conpty: close kills the child tree", ConPtyTests.Close_KillsTheChildTree),
        ("conpty: timeout on a silent child", ConPtyTests.Timeout_OnSilentHang),
        ("conpty: resize round-trip", ConPtyTests.Resize_RoundTrip),
        ("conpty: a concurrent send returns the active send", ConPtyTests.ConcurrentSend_ReturnsTheActiveSend),
        ("conpty: dispose closes live sessions", ConPtyTests.Dispose_ClosesLiveSessions),
    };

    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("FAKE_PTY_CHILD") == "1")
        {
            return FakePtyChild.Run();
        }
        var suites = new List<(string Name, Func<Task> Run)>(Suites);
        if (OperatingSystem.IsWindows())
        {
            if (ConPtyHostProbe.CanHostConsoleChild())
            {
                suites.AddRange(ConPtySuites);
            }
            else
            {
                // Some Windows hosts (observed on Windows 11 build 26200) fail every
                // console-subsystem child under a pseudo console with STATUS_DLL_INIT_FAILED
                // while GUI children attach fine — a host console issue, not an API misuse.
                foreach (var (name, _) in ConPtySuites)
                {
                    Console.WriteLine($"SKIP  {name} (ConPTY console child cannot start on this host)");
                }
            }
        }
        var previousFakePtyChild = Environment.GetEnvironmentVariable("FAKE_PTY_CHILD");
        Environment.SetEnvironmentVariable("FAKE_PTY_CHILD", "1");
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in suites)
        {
            try
            {
                run().GetAwaiter().GetResult();
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL  {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        Environment.SetEnvironmentVariable("FAKE_PTY_CHILD", previousFakePtyChild);
        return failures.Count == 0 ? 0 : 1;
    }
}
