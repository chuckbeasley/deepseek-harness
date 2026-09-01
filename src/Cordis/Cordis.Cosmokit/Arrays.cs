using System.Collections;

namespace Harness.Cordis.Cosmokit;

/// <summary>Array set and normalization helpers (port of cosmokit <c>array.ts</c>).</summary>
public static class Arrays
{
    /// <summary>Returns <c>true</c> when every item in <paramref name="array2"/> is present in <paramref name="array1"/>.</summary>
    public static bool Contain<T>(IEnumerable<T> array1, IEnumerable<T> array2)
    {
        var set = new HashSet<T>(array1);
        return array2.All(set.Contains);
    }

    /// <summary>Returns items that appear in both arrays.</summary>
    public static List<T> Intersection<T>(IEnumerable<T> array1, IEnumerable<T> array2)
    {
        var set = new HashSet<T>(array2);
        return array1.Where(set.Contains).ToList();
    }

    /// <summary>Returns items from <paramref name="array1"/> that do not appear in <paramref name="array2"/>.</summary>
    public static List<T> Difference<T>(IEnumerable<T> array1, IEnumerable<T> array2)
    {
        var set = new HashSet<T>(array2);
        return array1.Where(item => !set.Contains(item)).ToList();
    }

    /// <summary>Returns the set-union of two arrays while preserving first-occurrence order.</summary>
    public static List<T> Union<T>(IEnumerable<T> array1, IEnumerable<T> array2)
    {
        var result = new List<T>();
        var seen = new HashSet<T>();
        foreach (var item in array1.Concat(array2))
        {
            if (seen.Add(item)) result.Add(item);
        }
        return result;
    }

    /// <summary>Removes duplicate values while preserving first-occurrence order.</summary>
    public static List<T> Deduplicate<T>(IEnumerable<T> array)
    {
        var seen = new HashSet<T>();
        return array.Where(seen.Add).ToList();
    }

    /// <summary>Removes one item from a list and reports whether it was found.</summary>
    public static bool Remove<T>(List<T> list, T item)
    {
        var index = list.IndexOf(item);
        if (index < 0) return false;
        list.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Normalizes null, scalar, or list input to a list: <c>null</c> becomes an
    /// empty list, an <see cref="IList"/> is copied, and anything else is
    /// wrapped as a single element.
    /// </summary>
    public static List<object?> MakeArray(object? source)
    {
        if (source is null) return new List<object?>();
        if (source is IList list) return list.Cast<object?>().ToList();
        return new List<object?> { source };
    }
}
