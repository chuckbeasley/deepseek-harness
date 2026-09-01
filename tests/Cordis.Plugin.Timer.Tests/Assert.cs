using System.Diagnostics.CodeAnalysis;

namespace Harness.Cordis.Plugin.Timer.Tests;

/// <summary>Thrown by <see cref="Assert"/> when a check fails; aborts the current test.</summary>
public sealed class AssertionException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public AssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>Minimal zero-dependency assertion helpers for the Phase 1 console test runner.</summary>
public static class Assert
{
    /// <summary>Fails the current test with <paramref name="message"/>.</summary>
    [DoesNotReturn]
    public static void Fail(string message) => throw new AssertionException(message);

    /// <summary>Asserts <paramref name="condition"/> is <c>true</c>.</summary>
    public static void True(bool condition, string? message = null)
    {
        if (!condition) Fail(message ?? "expected true but got false");
    }

    /// <summary>Asserts <paramref name="condition"/> is <c>false</c>.</summary>
    public static void False(bool condition, string? message = null)
    {
        if (condition) Fail(message ?? "expected false but got true");
    }

    /// <summary>Asserts <paramref name="value"/> is <c>null</c>.</summary>
    public static void Null(object? value, string? message = null)
    {
        if (value is not null) Fail(message ?? $"expected null but got {value}");
    }

    /// <summary>Asserts <paramref name="value"/> is not <c>null</c>.</summary>
    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) Fail(message ?? "expected a non-null value");
    }

    /// <summary>Asserts <paramref name="actual"/> equals <paramref name="expected"/>.</summary>
    public static void Equal(object? expected, object? actual, string? message = null)
    {
        if (!Equals(expected, actual)) Fail(message ?? $"expected {expected} but got {actual}");
    }

    /// <summary>Asserts <paramref name="actual"/> does not equal <paramref name="expected"/>.</summary>
    public static void NotEqual(object? expected, object? actual, string? message = null)
    {
        if (Equals(expected, actual)) Fail(message ?? $"expected {expected} to differ from {actual}");
    }

    /// <summary>Asserts <paramref name="items"/> has no elements.</summary>
    public static void Empty<T>(IEnumerable<T> items, string? message = null)
    {
        if (items.Any()) Fail(message ?? "expected an empty collection");
    }

    /// <summary>Asserts <paramref name="haystack"/> contains <paramref name="needle"/>.</summary>
    public static void Contains(string needle, string haystack, string? message = null)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            Fail(message ?? $"expected \"{haystack}\" to contain \"{needle}\"");
        }
    }

    /// <summary>Asserts <paramref name="value"/> is of type <typeparamref name="T"/> and returns it.</summary>
    public static T IsType<T>(object? value, string? message = null)
    {
        if (value is T typed) return typed;
        Fail(message ?? $"expected {typeof(T).Name} but got {(value is null ? "null" : value.GetType().Name)}");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Asserts <paramref name="action"/> throws <typeparamref name="TException"/> and returns it.</summary>
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

    /// <summary>Asserts <paramref name="action"/> faults with <typeparamref name="TException"/> and returns it.</summary>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string? message = null)
        where TException : Exception
    {
        try
        {
            await action();
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
