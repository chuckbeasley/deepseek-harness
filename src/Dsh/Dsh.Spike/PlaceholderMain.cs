namespace Dsh.Spike;

/// <summary>
/// Part-1 stand-in entry point: Dsh.Spike is an Exe, but the real boot (Program.cs +
/// HeadlessTurnDriver) arrives in part 2 with the Cordis wiring. This placeholder keeps the Exe
/// buildable and is replaced in part 2.
/// </summary>
internal static class PlaceholderMain
{
    private static void Main()
    {
        Console.Error.WriteLine("Dsh.Spike: the headless boot arrives with the Cordis wiring (part 2).");
    }
}
