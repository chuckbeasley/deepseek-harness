namespace Harness.Preset.Tests;

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

    public static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
    {
        if (expected.Count != actual.Count || !expected.SequenceEqual(actual))
        {
            throw new AssertionException($"{message} (expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}])");
        }
    }

    /// <summary>Find the one roster row with <paramref name="id"/>; a missing row is a test failure.</summary>
    public static PresetInfo SinglePreset(IReadOnlyList<PresetInfo> presets, string id)
    {
        var match = presets.SingleOrDefault(preset => preset.Id == id);
        if (match is null)
        {
            throw new AssertionException($"expected exactly one preset \"{id}\" on the roster");
        }
        return match;
    }

    public static TException Throws<TException>(Action action, string message) where TException : Exception
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
            throw new AssertionException($"{message} (threw {error.GetType().Name} instead: {error.Message})");
        }
        throw new AssertionException($"{message} (nothing was thrown)");
    }
}
