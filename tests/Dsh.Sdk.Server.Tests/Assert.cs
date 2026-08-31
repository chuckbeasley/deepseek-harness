namespace Dsh.Sdk.Server.Tests;

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

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} (expected \"{expected}\", got \"{actual}\")");
        }
    }

    public static void Null(object? value, string message)
    {
        if (value is not null) throw new AssertionException($"{message} (got \"{value}\")");
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null) throw new AssertionException($"{message} (got null)");
    }

    public static void Contains(string expectedSubstring, string actual, string message)
    {
        if (actual.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
        {
            throw new AssertionException($"{message} (\"{actual}\" does not contain \"{expectedSubstring}\")");
        }
    }

    public static TException ThrowsAny<TException>(Func<Task> action, string message) where TException : Exception
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (TException error)
        {
            return error;
        }
        catch (Exception error)
        {
            throw new AssertionException($"{message} (threw {error.GetType().Name} instead: {error.Message})");
        }
        throw new AssertionException($"{message} (nothing was thrown)");
    }

    public static void WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new AssertionException("condition not met within timeout");
            }
            Thread.Sleep(10);
        }
    }
}
