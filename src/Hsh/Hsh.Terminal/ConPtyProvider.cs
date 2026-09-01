using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Harness.Cordis.Core;
using Microsoft.Win32.SafeHandles;

namespace Harness.Terminal;

/// <summary>Deployment-varying ConPTY backend config; no tunable is hardcoded.</summary>
public sealed record ConPtyConfig(
    /// <summary>The shell executable; defaults to <c>cmd.exe</c> (Windows).</summary>
    string? ShellPath = null,
    /// <summary>Initial console columns.</summary>
    int Cols = 160,
    /// <summary>Initial console rows.</summary>
    int Rows = 40,
    /// <summary>Absolute send timeout.</summary>
    int TimeoutMs = 30000,
    /// <summary>Quiet window after which a send without a prompt marker settles <c>inferred_idle</c>.</summary>
    int IdleSilenceMs = 3000,
    /// <summary>Bounded scrollback line ceiling.</summary>
    int ScrollbackLines = 500,
    /// <summary>Working directory for the shell; defaults to the current directory.</summary>
    string? Cwd = null);

/// <summary>
/// The ConPTY terminal provider (ctx.terminal; backend type <c>conpty</c>): real TTY semantics on
/// Windows through the pseudo-console API â€” the deferred PTY backend of the seam. The shell runs
/// under a controlled prompt (<c>ESC ] 133;D; ESC \ hsh&gt; </c>) so sends settle on prompt
/// evidence; output passes through the sanitizer into a bounded scrollback. Unix pty support stays
/// deferred (documented in the seam sources).
/// </summary>
public sealed class ConPtyTerminalProvider : Service, ITerminalService
{
    /// <summary>The backend type this provider registers.</summary>
    public const string BackendType = "conpty";

    private readonly object _gate = new();
    private readonly List<ConPtySession> _sessions = new();
    private readonly ConPtyConfig _config;
    private int _counter;

    /// <summary>Create the provider and register it as <c>terminal</c>.</summary>
    public ConPtyTerminalProvider(Context ctx, ConPtyConfig? config = null)
        : base(ctx, "terminal")
    {
        _config = config ?? new ConPtyConfig();
    }

    /// <inheritdoc />
    public Task<ITerminalSession> OpenAsync(TerminalOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type != BackendType)
        {
            throw new InvalidOperationException($"terminal: unknown backend type {request.Type} (registered: {BackendType})");
        }
        var child = ConPtyChild.Spawn(_config, request.Cwd);
        int id;
        lock (_gate) id = ++_counter;
        var session = new ConPtySession(new TerminalSessionId($"conpty-{id}"), request.Name, child, _config);
        lock (_gate) _sessions.Add(session);
        _ = session.Done.ContinueWith(_ =>
        {
            lock (_gate) _sessions.Remove(session);
        }, TaskScheduler.Default);
        session.Start();
        return Task.FromResult<ITerminalSession>(session);
    }

    /// <inheritdoc />
    public IReadOnlyList<TerminalSessionSnapshot> List()
    {
        lock (_gate) return _sessions.Select(session => session.Snapshot()).ToArray();
    }

    /// <summary>Teardown: close and await every live session.</summary>
    public override async ValueTask StopAsync()
    {
        ConPtySession[] live;
        lock (_gate) live = _sessions.ToArray();
        foreach (var session in live)
        {
            await session.CloseAsync("terminal service disposed");
        }
        await base.StopAsync();
    }
}

/// <summary>Win32 surface of the pseudo-console API.</summary>
internal static class ConPtyNative
{
    internal const uint ProcThreadAttributePseudoConsole = 0x20016;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint HandleFlagInherit = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartUpInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Ptr;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartUpInfoEx
    {
        public StartUpInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(in Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, in Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessW(string? lpApplicationName, string? lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartUpInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// One ConPTY child: the pseudo console, its pipes, and the shell process. Owns every native
/// handle; teardown closes the pseudo console first (terminating attached clients), then kills
/// the process tree, then releases the pipes and the attribute list.
/// </summary>
internal sealed class ConPtyChild : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IntPtr _hPc;
    private IntPtr _hInWrite;
    private IntPtr _hOutRead;
    private IntPtr _attrList;
    private IntPtr _attrListMemory;
    private FileStream? _input;
    private FileStream? _output;
    private Process? _process;
    private readonly TaskCompletionSource<SubprocessTerminalOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ConPtyChild()
    {
    }

    /// <summary>The shell process, or <c>null</c> when the spawn failed before process creation.</summary>
    public Process? Process => _process;

    /// <summary>Resolves at shell exit with the exit facts.</summary>
    public Task<SubprocessTerminalOutcome> Done => _done.Task;

    /// <summary>The async output stream (raw ConPTY bytes).</summary>
    public FileStream Output => _output!;

    /// <summary>Spawn the shell under a fresh pseudo console.</summary>
    public static ConPtyChild Spawn(ConPtyConfig config, string? requestCwd)
    {
        var child = new ConPtyChild();
        try
        {
            child.SpawnCore(config, requestCwd);
        }
        catch
        {
            child.Dispose();
            throw;
        }
        return child;
    }

    private void SpawnCore(ConPtyConfig config, string? requestCwd)
    {
        if (ConPtyNative.CreatePipe(out var hInRead, out var hInWrite, IntPtr.Zero, 0) == false)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");
        }
        _hInWrite = hInWrite;
        if (ConPtyNative.CreatePipe(out var hOutRead, out var hOutWrite, IntPtr.Zero, 0) == false)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");
        }
        _hOutRead = hOutRead;
        ConPtyNative.SetHandleInformation(hInRead, ConPtyNative.HandleFlagInherit, 0);
        ConPtyNative.SetHandleInformation(hInWrite, ConPtyNative.HandleFlagInherit, 0);
        ConPtyNative.SetHandleInformation(hOutRead, ConPtyNative.HandleFlagInherit, 0);
        ConPtyNative.SetHandleInformation(hOutWrite, ConPtyNative.HandleFlagInherit, 0);

        var size = new ConPtyNative.Coord { X = (short)config.Cols, Y = (short)config.Rows };
        var hr = ConPtyNative.CreatePseudoConsole(size, hInRead, hOutWrite, 0, out _hPc);
        // The pseudo console owns its references; the parent sides close now.
        ConPtyNative.CloseHandle(hInRead);
        ConPtyNative.CloseHandle(hOutWrite);
        if (hr != 0)
        {
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        // Attribute list carrying the pseudo console into the child.
        var listSize = IntPtr.Zero;
        ConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        _attrListMemory = Marshal.AllocHGlobal(listSize);
        _attrList = _attrListMemory;
        if (ConPtyNative.InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize) == false)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
        }
        if (ConPtyNative.UpdateProcThreadAttribute(
            _attrList, 0, (IntPtr)ConPtyNative.ProcThreadAttributePseudoConsole, _hPc,
            (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) == false)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
        }

        // The controlled prompt through cmd's PROMPT metavariable: ESC ] 133;D; ESC \ hsh> .
        var prompt = "\u001b]133;D;\u001b\\hsh$G ";
        var startup = new ConPtyNative.StartUpInfoEx();
        startup.StartupInfo.Cb = Marshal.SizeOf<ConPtyNative.StartUpInfoEx>();
        startup.AttributeList = _attrList;

        var shell = config.ShellPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var envBlock = BuildEnvironmentBlock("\u001b]133;D;\u001b\\hsh$G ");
        var cwd = string.IsNullOrEmpty(requestCwd) ? (config.Cwd ?? Environment.CurrentDirectory) : requestCwd;
        // The canonical ConPTY shape (per the Microsoft pseudo-console sample): lpApplicationName
        // NULL and the full command line, bInheritHandles FALSE — the attribute list duplicates
        // the pseudo console into the child, so no other handle leaks in.
        var commandLine = shell.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet \"{shell}\""
            : $"\"{shell}\"";
        var ok = ConPtyNative.CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            bInheritHandles: false,
            ConPtyNative.ExtendedStartupInfoPresent | ConPtyNative.CreateUnicodeEnvironment,
            envBlock,
            cwd,
            ref startup,
            out var pi);
        Marshal.FreeHGlobal(envBlock);
        if (ok == false)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed for {shell}");
        }
        ConPtyNative.CloseHandle(pi.Thread);
        _process = Process.GetProcessById(pi.ProcessId);
        _process.EnableRaisingEvents = true;
        // CreatePipe produces synchronous handles: no overlapped FileStream, so reads and writes
        // run on dedicated threads under the write lock (the ConPTY I/O is small and infrequent).
        _input = new FileStream(new SafeFileHandle(_hInWrite, ownsHandle: true), FileAccess.Write, bufferSize: 0);
        _hInWrite = IntPtr.Zero;
        _output = new FileStream(new SafeFileHandle(_hOutRead, ownsHandle: true), FileAccess.Read, bufferSize: 0);
        _hOutRead = IntPtr.Zero;
        _ = _process.WaitForExitAsync().ContinueWith(_ =>
        {
            try
            {
                _done.TrySetResult(new SubprocessTerminalOutcome(_process.ExitCode, null));
            }
            catch (InvalidOperationException)
            {
                _done.TrySetResult(new SubprocessTerminalOutcome(null, null));
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Write input bytes to the console (serialized with resize).</summary>
    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_input is null) throw new InvalidOperationException("terminal: the ConPTY input pipe is closed");
            await Task.Run(() => _input.Write(bytes, 0, bytes.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Resize the console (serialized with input writes; a failure leaves the size unchanged).</summary>
    public void Resize(int cols, int rows)
    {
        _writeLock.Wait();
        try
        {
            if (_hPc == IntPtr.Zero) return;
            var size = new ConPtyNative.Coord { X = (short)cols, Y = (short)rows };
            _ = ConPtyNative.ResizePseudoConsole(_hPc, size);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Teardown ladder: close the pseudo console, kill the tree, wait for exit, release handles.</summary>
    public void Dispose()
    {
        if (_hPc != IntPtr.Zero)
        {
            ConPtyNative.ClosePseudoConsole(_hPc);
            _hPc = IntPtr.Zero;
        }
        if (_process is not null)
        {
            ProcessTreeKill(_process);
            try
            {
                _process.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            _process.Dispose();
        }
        _input?.Dispose();
        _output?.Dispose();
        if (_hInWrite != IntPtr.Zero) ConPtyNative.CloseHandle(_hInWrite);
        if (_hOutRead != IntPtr.Zero) ConPtyNative.CloseHandle(_hOutRead);
        if (_attrList != IntPtr.Zero)
        {
            ConPtyNative.DeleteProcThreadAttributeList(_attrList);
            _attrList = IntPtr.Zero;
        }
        if (_attrListMemory != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_attrListMemory);
            _attrListMemory = IntPtr.Zero;
        }
        _writeLock.Dispose();
    }

    /// <summary>Kill the whole tree rooted at the shell (idempotent).</summary>
    private static void ProcessTreeKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (Win32Exception)
        {
            // tree gone
        }
    }

    /// <summary>
    /// The child environment block: the parent environment plus the controlled PROMPT and the
    /// dumb-terminal facts. UTF-16, sorted, double-null terminated (CREATE_UNICODE_ENVIRONMENT).
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(string prompt)
    {
        var entries = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => $"{entry.Key}={entry.Value}")
            .ToList();
        entries.Add("PROMPT=" + prompt);
        entries.Add("TERM=dumb");
        entries.Add("PAGER=cat");
        entries.Add("GIT_PAGER=cat");
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        var block = string.Join('\0', entries) + "\0\0";
        var bytes = Encoding.Unicode.GetBytes(block);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return memory;
    }
}


