namespace Dsh.Spike;

/// <summary>Headless console boot: runs the Phase 0 smoke scenario and exits non-zero on failure.</summary>
public static class Program
{
    public static int Main()
    {
        try
        {
            SmokeScenario.RunAsync(Console.Out).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Dsh.Spike smoke failed: {error.Message}");
            return 1;
        }
    }
}

