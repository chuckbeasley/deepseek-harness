namespace Dsh.Webhook.Tests;

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

    /// <summary>
    /// Asserts <paramref name="actual"/> equals <paramref name="expected"/>: default equality for
    /// scalars, element-wise deep equality for collections.
    /// </summary>
    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!DeepEqual(expected, actual))
        {
            Fail(message ?? $"expected {expected} but got {actual}");
        }
    }

    private static bool DeepEqual(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected is null || actual is null) return false;
        if (expected is string expectedString && actual is string actualString) return expectedString == actualString;
        if (expected is System.Collections.IEnumerable expectedItems && actual is System.Collections.IEnumerable actualItems)
        {
            var left = expectedItems.Cast<object?>().ToArray();
            var right = actualItems.Cast<object?>().ToArray();
            if (left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (!DeepEqual(left[index], right[index])) return false;
            }
            return true;
        }
        return EqualityComparer<object?>.Default.Equals(expected, actual);
    }

    /// <summary>Asserts <paramref name="actual"/> contains <paramref name="needle"/>.</summary>
    public static void Contains(string needle, string actual, string? message = null)
    {
        if (actual is null || !actual.Contains(needle, StringComparison.Ordinal))
        {
            Fail(message ?? $"expected \"{actual}\" to contain \"{needle}\"");
        }
    }

    /// <summary>Asserts <paramref name="check"/> succeeds for every item.</summary>
    public static void All<T>(IEnumerable<T> items, Action<T> check, string? message = null)
    {
        foreach (var item in items)
        {
            check(item);
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

    /// <summary>Asserts the async <paramref name="action"/> throws <typeparamref name="TException"/> and returns it.</summary>
    public static TException ThrowsAny<TException>(Func<Task> action, string? message = null)
        where TException : Exception
    {
        try
        {
            action().GetAwaiter().GetResult();
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

    /// <summary>Poll <paramref name="condition"/> until it holds or <paramref name="timeoutMs"/> elapses.</summary>
    public static void WaitUntil(Func<bool> condition, int timeoutMs = 5000, string? message = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Fail(message ?? "condition not met within timeout");
            }
            Thread.Sleep(5);
        }
    }
}

