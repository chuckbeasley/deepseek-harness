using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Cordis.Cosmokit;

/// <summary>Shared nullability, object, and dictionary helpers (port of cosmokit <c>misc.ts</c>).</summary>
public static class Misc
{
    /// <summary>Returns <c>true</c> when <paramref name="value"/> is <c>null</c> (the C# equivalent of JS nullish).</summary>
    public static bool IsNullable([NotNullWhen(false)] object? value) => value is null;

    /// <summary>Returns <c>true</c> when <paramref name="value"/> is not <c>null</c>.</summary>
    public static bool IsNonNullable(object? value) => value is not null;

    /// <summary>
    /// Returns <c>true</c> for any non-null, non-string, non-list value. This
    /// mirrors the loose <c>isPlainObject</c> check: any object that is not an
    /// array counts, including dictionaries and plain POCOs.
    /// </summary>
    public static bool IsPlainObject(object? data)
        => data is not null && data is not string && data is not IList;

    /// <summary>
    /// Returns <c>true</c> for CLR numbers (integral and floating point).
    /// <see cref="bool"/> is deliberately excluded, matching JS <c>typeof</c>.
    /// </summary>
    public static bool IsNumeric(object? value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>
    /// Normalizes a plain value to a <see cref="Dictionary{TKey,TValue}"/>.
    /// Dictionaries are used directly (so property write-back reaches the
    /// caller's instance); other non-list objects are read through their public
    /// instance properties (a POCO adapter); anything else returns <c>null</c>.
    /// </summary>
    public static Dictionary<string, object?>? ToPlainDictionary(object? data)
    {
        if (data is Dictionary<string, object?> dict) return dict;
        if (data is IDictionary<string, object?> generic) return new Dictionary<string, object?>(generic);
        if (data is IDictionary other)
        {
            var result = new Dictionary<string, object?>(other.Count);
            foreach (DictionaryEntry entry in other)
            {
                result[entry.Key.ToString() ?? string.Empty] = entry.Value;
            }
            return result;
        }
        if (data is not null && data is not string && data is not IList)
        {
            var result = new Dictionary<string, object?>();
            var type = data.GetType();
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                try
                {
                    result[property.Name] = property.GetValue(data);
                }
                catch
                {
                    // An unreadable property is skipped; the remaining properties still form a plain object.
                }
            }
            return result;
        }
        return null;
    }

    /// <summary>Returns a new dictionary containing the entries whose key passes <paramref name="filter"/>.</summary>
    public static Dictionary<string, object?> FilterKeys(IDictionary source, Func<string, object?, bool> filter)
    {
        var result = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in source)
        {
            var key = entry.Key.ToString() ?? string.Empty;
            if (filter(key, entry.Value)) result[key] = entry.Value;
        }
        return result;
    }

    /// <summary>Maps dictionary values while preserving the original key set.</summary>
    public static Dictionary<string, TOut> MapValues<TIn, TOut>(IDictionary<string, TIn> source, Func<TIn, string, TOut> transform)
    {
        var result = new Dictionary<string, TOut>(source.Count);
        foreach (var pair in source)
        {
            result[pair.Key] = transform(pair.Value, pair.Key);
        }
        return result;
    }

    /// <summary>Alias for <see cref="MapValues{TIn,TOut}"/> matching the cosmokit <c>valueMap</c> name.</summary>
    public static Dictionary<string, TOut> ValueMap<TIn, TOut>(IDictionary<string, TIn> source, Func<TIn, string, TOut> transform)
        => MapValues(source, transform);

    /// <summary>Picks the given keys from <paramref name="source"/>; without keys, copies the whole source.</summary>
    public static Dictionary<string, object?> Pick(IDictionary<string, object?> source, IEnumerable<string>? keys = null, bool forced = false)
    {
        var result = new Dictionary<string, object?>();
        if (keys is null)
        {
            foreach (var pair in source) result[pair.Key] = pair.Value;
            return result;
        }
        foreach (var key in keys)
        {
            object? value = null;
            if (source.TryGetValue(key, out value) || forced) result[key] = value;
        }
        return result;
    }

    /// <summary>Omitts the given keys from a shallow copy of <paramref name="source"/>.</summary>
    public static Dictionary<string, object?> Omit(IDictionary<string, object?> source, IEnumerable<string>? keys = null)
    {
        var result = new Dictionary<string, object?>(source);
        if (keys is not null)
        {
            foreach (var key in keys) result.Remove(key);
        }
        return result;
    }
}




