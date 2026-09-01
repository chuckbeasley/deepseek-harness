using System.Diagnostics.CodeAnalysis;

namespace Harness.Cordis.Plugin.Hmr.Tests;

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

    /// <summary>Asserts <paramref name="actual"/> equals <paramref name="expected"/>.</summary>
    public static void Equal(object? expected, object? actual, string? message = null)
    {
        if (!Equals(expected, actual)) Fail(message ?? $"expected {expected} but got {actual}");
    }

    /// <summary>Asserts <paramref name="haystack"/> contains <paramref name="needle"/>.</summary>
    public static void Contains(string needle, string haystack, string? message = null)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            Fail(message ?? $"expected \"{haystack}\" to contain \"{needle}\"");
        }
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
}
