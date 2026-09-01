namespace Harness.Terminal.Tests;

/// <summary>Test failure carrying one assertion message.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>Zero-dependency console assertion helpers.</summary>
public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void NotNull(object? value, string message)
    {
        if (value is null) throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} (expected \"{expected}\", got \"{actual}\")");
        }
    }

    /// <summary>Poll <paramref name="condition"/> until it holds or <paramref name="timeoutMs"/> elapses.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string message)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new AssertionException(message);
            }
            await Task.Delay(100);
        }
    }
}
