using System.Text;

namespace Dsh.Sandbox.Tests;

/// <summary>
/// Scripted stand-in for the landlock-run sidecar, entered via <c>--fake-landlock-run</c> on the
/// test assembly: answers <c>--probe</c> with the scripted enforcement line and records wrapped
/// argv to the <c>FAKE_RECORD_ARGV</c> file. The probe line defaults to full enforcement and is
/// overridable through <c>FAKE_PROBE_OUTPUT</c>.
/// </summary>
public static class FakeLandlockRun
{
    public static int Run()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).Where(argument => argument != "--fake-landlock-run").ToArray();
        var probeOutput = Environment.GetEnvironmentVariable("FAKE_PROBE_OUTPUT");
        if (args.Length > 0 && args[0] == "--probe")
        {
            Console.Out.WriteLine(probeOutput ?? "landlock: fully enforced");
            return 0;
        }
        var record = Environment.GetEnvironmentVariable("FAKE_RECORD_ARGV");
        if (record is { Length: > 0 })
        {
            File.AppendAllText(record, string.Join(" ", args.Select(Quote)) + "\n", Encoding.UTF8);
        }
        return 0;
    }

    private static string Quote(string argument)
        => argument.Contains(' ') ? "\"" + argument + "\"" : argument;
}
