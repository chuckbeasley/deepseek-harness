using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Terminal.Tests;

/// <summary>
/// Scripted ConPTY client for the terminal tests, entered when <c>FAKE_PTY_CHILD=1</c> in the
/// environment: reads raw stdin bytes and echoes <c>ECHO_HEX:&lt;hex&gt;</c> per line (the exact
/// bytes including the line terminator), answers <c>SIZE</c> with the console buffer size
/// (<c>SIZE:&lt;cols&gt;x&lt;rows&gt;</c>), stays silent on <c>SILENT</c>, and exits on
/// <c>QUIT</c>/stdin EOF.
/// </summary>
public static class FakePtyChild
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public short Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    private const int StdOutputHandle = -11;

    public static int Run()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var line = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = stdin.Read(buffer, 0, 1);
            if (read == 0) return 0;
            line.Add(buffer[0]);
            if (buffer[0] is (byte)'\r' or (byte)'\n')
            {
                var text = Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r', '\n');
                if (text == "SIZE")
                {
                    var size = CurrentSize();
                    var bytes = Encoding.ASCII.GetBytes($"SIZE:{size.X}x{size.Y}\r\n");
                    stdout.Write(bytes, 0, bytes.Length);
                    stdout.Flush();
                }
                else if (text == "QUIT")
                {
                    return 0;
                }
                else if (text != "SILENT")
                {
                    var hex = Convert.ToHexString(line.ToArray());
                    var bytes = Encoding.ASCII.GetBytes($"ECHO_HEX:{hex}\r\n");
                    stdout.Write(bytes, 0, bytes.Length);
                    stdout.Flush();
                }
                line.Clear();
            }
        }
    }

    private static Coord CurrentSize()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (GetConsoleScreenBufferInfo(handle, out var info)) return info.Size;
        return new Coord { X = 0, Y = 0 };
    }
}
