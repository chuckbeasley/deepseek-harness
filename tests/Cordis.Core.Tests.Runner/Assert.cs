namespace Cordis.Core.Tests.Runner;

/// <summary>
/// Minimal assertion helpers for the zero-dependency console runner (compiled with a bare csc
/// invocation; no test framework assemblies are referenced).
/// </summary>
internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new AssertionException(message ?? "expected true, got false");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new AssertionException(message ?? "expected false, got true");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null) throw new AssertionException(message ?? $"expected null, got {Format(value)}");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) throw new AssertionException(message ?? "expected non-null");
    }

    public static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual))
            throw new AssertionException($"expected same instance {Format(expected)}, got {Format(actual)}");
    }

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!AreEqual(expected, actual))
            throw new AssertionException($"expected {Format(expected)}, got {Format(actual)}");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!AreEqual(expected, actual))
            throw new AssertionException($"expected {Format(expected)}, got {Format(actual)}");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
    {
        if (!collection.Any(item => AreEqual(item, expected)))
            throw new AssertionException($"expected collection to contain {Format(expected)}");
    }

    public static void DoesNotContain<T>(T notExpected, IEnumerable<T> collection)
    {
        if (collection.Any(item => AreEqual(item, notExpected)))
            throw new AssertionException($"did not expect collection to contain {Format(notExpected)}");
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var list = collection.ToList();
        if (list.Count != 1)
            throw new AssertionException($"expected exactly one item, got {list.Count}");
        return list[0];
    }

    public static T IsType<T>(object? value)
    {
        if (value is not T typed)
            throw new AssertionException($"expected type {typeof(T).Name}, got {value?.GetType().Name ?? "null"}");
        return typed;
    }

    public static TException Throws<TException>(Action action) where TException : Exception
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
            throw new AssertionException($"expected {typeof(TException).Name}, got {error.GetType().Name}: {error.Message}");
        }
        throw new AssertionException($"expected {typeof(TException).Name}, no exception was thrown");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
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
            throw new AssertionException($"expected {typeof(TException).Name}, got {error.GetType().Name}: {error.Message}");
        }
        throw new AssertionException($"expected {typeof(TException).Name}, no exception was thrown");
    }

    private static bool AreEqual(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected is null || actual is null) return false;
        if (expected is string || actual is string) return expected.Equals(actual);
        if (expected is System.Collections.IEnumerable exp && actual is System.Collections.IEnumerable act)
        {
            var e = exp.Cast<object?>().ToList();
            var a = act.Cast<object?>().ToList();
            if (e.Count != a.Count) return false;
            for (int i = 0; i < e.Count; i++)
            {
                if (!AreEqual(e[i], a[i])) return false;
            }
            return true;
        }
        return expected.Equals(actual);
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        System.Collections.IEnumerable seq => "[" + string.Join(", ", seq.Cast<object?>().Select(Format)) + "]",
        _ => value.ToString() ?? value.GetType().Name,
    };
}

internal sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

