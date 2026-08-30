using System.Collections;

namespace Cordis.Plugin.Loader;

/// <summary>
/// Deep structural equality for entry option diffs (C# adaptation of cosmokit <c>deepEqual</c>,
/// kept local because the loader references only Cordis.Core). Handles primitives, strings,
/// dictionaries, and enumerables; other reference types compare by <c>Equals</c>.
/// </summary>
internal static class Deep
{
    /// <summary>Return whether two option values are deeply equal.</summary>
    public static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is IDictionary da && b is IDictionary db) return DictionariesEqual(da, db);
        if (a is IEnumerable ea && b is IEnumerable eb) return EnumerablesEqual(ea, eb);
        if (a.GetType().IsValueType) return a.Equals(b);
        return a.Equals(b);
    }

    private static bool DictionariesEqual(IDictionary a, IDictionary b)
    {
        if (a.Count != b.Count) return false;
        foreach (DictionaryEntry entry in a)
        {
            if (!b.Contains(entry.Key)) return false;
            if (!ValueEquals(entry.Value, b[entry.Key])) return false;
        }
        return true;
    }

    private static bool EnumerablesEqual(IEnumerable a, IEnumerable b)
    {
        var listA = a.Cast<object?>().ToList();
        var listB = b.Cast<object?>().ToList();
        if (listA.Count != listB.Count) return false;
        for (var i = 0; i < listA.Count; i++)
        {
            if (!ValueEquals(listA[i], listB[i])) return false;
        }
        return true;
    }
}
