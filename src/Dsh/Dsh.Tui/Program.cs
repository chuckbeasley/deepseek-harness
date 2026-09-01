namespace Harness.Tui;

/// <summary>Standalone TUI entry (the CLI's tui profile calls <see cref="TuiApp.Run"/>).</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return TuiApp.Run(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"dsh tui failed: {error.Message}");
            return 1;
        }
    }
}
