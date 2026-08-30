using System.Collections;
using System.Globalization;

namespace Cordis.Cosmokit;

/// <summary>Deep-clone and deep-equality helpers (port of cosmokit <c>types.ts</c>).</summary>
public static class Deep
{
    /// <summary>
    /// Deep-clones common values: scalars are returned as-is, arrays and lists
    /// become element-wise copies, dictionaries become
    /// <see cref="Dictionary{TKey,TValue}"/> copies, and <see cref="DateTime"/>
    /// values are copied. Cyclic structures are preserved via a reference map.
    /// Unknown object types are returned as-is (no prototype cloning).
    /// </summary>
    public static object? Clone(object? source) => Clone(source, new Dictionary<object, object?>(ReferenceEqualityComparer.Instance));

    private static object? Clone(object? source, Dictionary<object, object?> refs)
    {
        if (source is null || source is string || Misc.IsNumeric(source) || source is bool || source is char)
        {
            return source;
        }
        if (source is DateTime date)
        {
            return new DateTime(date.Ticks, date.Kind);
        }
        if (source is Array array)
        {
            if (refs.TryGetValue(source, out var cached)) return cached;
            var result = new object?[array.Length];
            refs[source] = result;
            for (var i = 0; i < array.Length; i++)
            {
                result[i] = Clone(array.GetValue(i), refs);
            }
            return result;
        }
        if (source is IList list)
        {
            if (refs.TryGetValue(source, out var cached)) return cached;
            var result = new List<object?>(list.Count);
            refs[source] = result;
            foreach (var item in list)
            {
                result.Add(Clone(item, refs));
            }
            return result;
        }
        if (source is IDictionary dict)
        {
            if (refs.TryGetValue(source, out var cached)) return cached;
            var result = new Dictionary<string, object?>(dict.Count);
            refs[source] = result;
            foreach (DictionaryEntry entry in dict)
            {
                result[entry.Key.ToString() ?? string.Empty] = Clone(entry.Value, refs);
            }
            return result;
        }
        return source;
    }

    /// <summary>
    /// Deeply compares arrays, lists, dictionaries, dates, and plain object
    /// fields. Numeric values of different CLR types compare equal, matching JS
    /// <c>1 === 1.0</c>. Without <paramref name="strict"/>, two nullish values
    /// compare equal.
    /// </summary>
    public static bool DeepEqual(object? a, object? b, bool strict = false)
    {
        if (Equals(a, b)) return true;
        if (Misc.IsNumeric(a) && Misc.IsNumeric(b))
        {
            return Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
        }
        if (!strict && Misc.IsNullable(a) && Misc.IsNullable(b)) return true;
        if (IsScalar(a) || IsScalar(b)) return false;
        if (a is null || b is null) return false;
        if (a is DateTime dateA && b is DateTime dateB) return dateA.Ticks == dateB.Ticks;
        if (a is IList listA && b is IList listB)
        {
            if (listA.Count != listB.Count) return false;
            for (var i = 0; i < listA.Count; i++)
            {
                if (!DeepEqual(listA[i], listB[i], strict)) return false;
            }
            return true;
        }
        if (a is IDictionary dictA && b is IDictionary dictB)
        {
            var keys = new HashSet<string>();
            foreach (DictionaryEntry entry in dictA) keys.Add(entry.Key.ToString() ?? string.Empty);
            foreach (DictionaryEntry entry in dictB) keys.Add(entry.Key.ToString() ?? string.Empty);
            foreach (var key in keys)
            {
                var valueA = dictA.Contains(key) ? dictA[key] : null;
                var valueB = dictB.Contains(key) ? dictB[key] : null;
                if (!DeepEqual(valueA, valueB, strict)) return false;
            }
            return true;
        }
        return false;
    }

    private static bool IsScalar(object? value)
        => value is null || value is string || Misc.IsNumeric(value) || value is bool || value is char;
}
