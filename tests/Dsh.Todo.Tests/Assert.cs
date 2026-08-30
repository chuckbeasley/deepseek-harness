namespace Dsh.Todo.Tests;

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

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} (expected \"{expected}\", got \"{actual}\")");
        }
    }

    public static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
    {
        if (expected.Count != actual.Count || !expected.SequenceEqual(actual))
        {
            throw new AssertionException($"{message} (expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}])");
        }
    }
}
