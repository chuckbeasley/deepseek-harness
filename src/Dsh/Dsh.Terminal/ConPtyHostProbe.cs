using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Terminal;

/// <summary>
/// Environment probe for ConPTY console children. Some Windows hosts (observed on Windows 11
/// build 26200) fail every console-subsystem child spawned under a pseudo console with
/// <c>STATUS_DLL_INIT_FAILED</c> (0xC0000142) while GUI children attach fine — a host console
/// issue, not an API misuse: the same spawn sequence serves a GUI child. Suites that need a live
/// console child probe once and skip with the reason when the host cannot host one.
/// </summary>
internal static class ConPtyHostProbe
{
    private const uint DllInitFailed = 0xC0000142;

    private static readonly object Gate = new();
    private static bool? _cached;

    /// <summary>Whether a console child can start under a pseudo console on this host.</summary>
    public static bool CanHostConsoleChild()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached.Value;
            _cached = ProbeOnce();
            return _cached.Value;
        }
    }

    private static bool ProbeOnce()
    {
        var hPc = IntPtr.Zero;
        var hInWrite = IntPtr.Zero;
        var hOutRead = IntPtr.Zero;
        var attrMemory = IntPtr.Zero;
        try
        {
            if (Native.CreatePipe(out var hInRead, out hInWrite, IntPtr.Zero, 0) == false) return false;
            if (Native.CreatePipe(out hOutRead, out var hOutWrite, IntPtr.Zero, 0) == false) return false;
            var hr = Native.CreatePseudoConsole(new Coord { X = 80, Y = 24 }, hInRead, hOutWrite, 0, out hPc);
            Native.CloseHandle(hInRead);
            Native.CloseHandle(hOutWrite);
            if (hr != 0) return false;

            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrMemory = Marshal.AllocHGlobal(size);
            if (Native.InitializeProcThreadAttributeList(attrMemory, 1, 0, ref size) == false) return false;
            if (Native.UpdateProcThreadAttribute(attrMemory, 0, (IntPtr)0x20016, hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) == false) return false;

            var startup = new StartUpInfoEx();
            startup.StartupInfo.Cb = Marshal.SizeOf<StartUpInfoEx>();
            startup.AttributeList = attrMemory;
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (Native.CreateProcessW(null, $"\"{shell}\"", IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, null, ref startup, out var pi) == false) return false;
            Native.CloseHandle(pi.Thread);
            var wait = Native.WaitForSingleObject(pi.Process, 1500);
            var healthy = wait != 0; // still running after the window: console init succeeded
            if (!healthy)
            {
                Native.GetExitCodeProcess(pi.Process, out var code);
                healthy = code != DllInitFailed;
            }
            Native.CloseHandle(pi.Process);
            return healthy;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (hPc != IntPtr.Zero) Native.ClosePseudoConsole(hPc);
            if (attrMemory != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attrMemory);
                Marshal.FreeHGlobal(attrMemory);
            }
            if (hInWrite != IntPtr.Zero) Native.CloseHandle(hInWrite);
            if (hOutRead != IntPtr.Zero) Native.CloseHandle(hOutRead);
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(in Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

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
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartUpInfo
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
    private struct StartUpInfoEx
    {
        public StartUpInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
