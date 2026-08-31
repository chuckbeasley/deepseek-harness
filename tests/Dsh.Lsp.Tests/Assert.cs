namespace Dsh.Lsp.Tests;

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

    public static void False(bool condition, string message)
    {
        if (condition) throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} (expected \"{expected}\", got \"{actual}\")");
        }
    }

    public static void Contains(string expectedSubstring, string actual, string message)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionException($"{message} (expected \"{actual}\" to contain \"{expectedSubstring}\")");
        }
    }

    /// <summary>Run <paramref name="action"/> and return the exception of type <typeparamref name="TException"/> it throws.</summary>
    public static TException Throws<TException>(Action action, string message = "expected an exception") where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        catch (Exception error)
        {
            throw new AssertionException($"{message} (got {error.GetType().Name}: {error.Message})");
        }
        throw new AssertionException($"{message} (no exception thrown)");
    }

    /// <summary>Await <paramref name="action"/> and return the exception of type <typeparamref name="TException"/> it throws.</summary>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string message = "expected an exception") where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }
        catch (Exception error)
        {
            throw new AssertionException($"{message} (got {error.GetType().Name}: {error.Message})");
        }
        throw new AssertionException($"{message} (no exception thrown)");
    }
}
