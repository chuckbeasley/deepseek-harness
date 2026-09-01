namespace Harness.Agent.Tests;

/// <summary>Minimal assertion helpers.</summary>
public static class Assert
{
    /// <summary>Assert that <paramref name="condition"/> holds.</summary>
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"expected true: {message}");
    }

    /// <summary>Assert reference or value equality.</summary>
    public static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    /// <summary>Assert that <paramref name="action"/> throws <typeparamref name="T"/>.</summary>
    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{message}: expected {typeof(T).Name}, got {error.GetType().Name}");
        }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}, nothing was thrown");
    }
}
