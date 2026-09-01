using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace Harness.Storage.Tests;

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
    /// <summary>Fails the current test with <paramref name="message"/>.</summary>
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

    /// <summary>Asserts <paramref name="expected"/> and <paramref name="actual"/> are the same instance.</summary>
    public static void Same(object? expected, object? actual, string? message = null)
    {
        if (!ReferenceEquals(expected, actual)) Fail(message ?? "expected the same instance");
    }

    /// <summary>Asserts <paramref name="actual"/> deep-equals <paramref name="expected"/> (structural, including collections).</summary>
    public static void Equal(object? expected, object? actual, string? message = null)
    {
        if (!DeepEqual(expected, actual)) Fail(message ?? $"expected {expected} but got {actual}");
    }

    /// <summary>Asserts <paramref name="actual"/> does not deep-equal <paramref name="expected"/>.</summary>
    public static void NotEqual(object? expected, object? actual, string? message = null)
    {
        if (DeepEqual(expected, actual)) Fail(message ?? $"expected {expected} to differ from {actual}");
    }

    /// <summary>Asserts <paramref name="items"/> has no elements.</summary>
    public static void Empty<T>(IEnumerable<T> items, string? message = null)
    {
        if (items.Any()) Fail(message ?? "expected an empty collection");
    }

    /// <summary>Asserts <paramref name="items"/> has exactly one element and returns it.</summary>
    public static T Single<T>(IEnumerable<T> items, string? message = null)
    {
        var list = items.ToList();
        if (list.Count != 1) Fail(message ?? $"expected exactly one item but got {list.Count}");
        return list[0];
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

    /// <summary>Asserts the async <paramref name="action"/> throws <typeparamref name="TException"/> or a derived type.</summary>
    public static void ThrowsAny<TException>(Func<Task> action, string? message = null)
        where TException : Exception
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception other)
        {
            throw new AssertionException(message ?? $"expected {typeof(TException).Name} but got {other.GetType().Name}: {other.Message}");
        }
        throw new AssertionException(message ?? $"expected {typeof(TException).Name} but nothing was thrown");
    }

    /// <summary>
    /// Structural equality: primitives by value, JsonElement by deep value, collections
    /// element-wise, and other objects by their public readable properties. Record-synthesized
    /// equality is reference-based for collection-typed properties, so round-trip tests use this.
    /// </summary>
    private static bool DeepEqual(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected is null || actual is null) return false;
        if (expected is string expectedString && actual is string actualString) return expectedString == actualString;
        if (expected is JsonElement expectedJson && actual is JsonElement actualJson) return expectedJson.Equals(actualJson);
        if (expected is IEnumerable expectedItems && actual is IEnumerable actualItems)
        {
            var left = expectedItems.Cast<object?>().ToArray();
            var right = actualItems.Cast<object?>().ToArray();
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
            {
                if (!DeepEqual(left[i], right[i])) return false;
            }
            return true;
        }
        if (expected.GetType() != actual.GetType()) return false;
        if (expected.GetType().IsValueType) return expected.Equals(actual);
        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            if (!DeepEqual(property.GetValue(expected), property.GetValue(actual))) return false;
        }
        return true;
    }
}
