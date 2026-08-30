namespace Dsh.Feedback.Tests;

/// <summary>Thrown by <see cref="Assert"/> when a check fails; aborts the current test.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>Minimal zero-dependency assertion helpers for the console test runner.</summary>
public static class Assert
{
    public static void Fail(string message) => throw new AssertionException(message);

    public static void True(bool condition, string? message = null)
    {
        if (!condition) Fail(message ?? "expected true but got false");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) Fail(message ?? "expected false but got true");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) Fail(message ?? "expected a non-null value");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null) Fail(message ?? $"expected null but got {value}");
    }

    public static void Same(object? expected, object? actual, string? message = null)
    {
        if (!ReferenceEquals(expected, actual)) Fail(message ?? "expected the same instance");
    }

    public static T IsType<T>(object? value, string? message = null)
    {
        if (value is T typed) return typed;
        Fail(message ?? $"expected {typeof(T).Name} but got {(value is null ? "null" : value.GetType().Name)}");
        throw new InvalidOperationException("unreachable");
    }

    public static void Empty<T>(IEnumerable<T> items, string? message = null)
    {
        if (items.Any()) Fail(message ?? "expected an empty collection");
    }

    public static T Single<T>(IEnumerable<T> items, string? message = null)
    {
        var list = items.ToList();
        if (list.Count != 1) Fail(message ?? $"expected exactly one item but got {list.Count}");
        return list[0];
    }

    public static void Equal(object? expected, object? actual, string? message = null)
    {
        if (!Equals(expected, actual)) Fail(message ?? $"expected {expected} but got {actual}");
    }

    public static TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception other)
        {
            throw new AssertionException(message ?? $"expected {typeof(TException).Name} but got {other.GetType().Name}: {other.Message}");
        }
        throw new AssertionException(message ?? $"expected {typeof(TException).Name} but nothing was thrown");
    }
}
