using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Dsh.Settings;

/// <summary>
/// Internal JSON-value helpers for the settings seam: plain-object detection, deep equality and
/// clone, JSON-shape write validation, layer merging, and JsonElement conversion. Values are
/// represented as <c>Dictionary&lt;string, object?&gt;</c> for objects, <c>List&lt;object?&gt;</c>
/// for arrays, and CLR scalars (string, bool, number, null) for leaves.
/// </summary>
internal static class SettingsJson
{
    /// <summary>Whether a value is a plain data object the seam may recurse into.</summary>
    public static bool IsPlainObject(object? value) => value is Dictionary<string, object?>;

    /// <summary>Deep-compare two JSON-shaped values.</summary>
    public static bool DeepEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left is string leftString && right is string rightString) return leftString == rightString;
        if (left is bool leftBool && right is bool rightBool) return leftBool == rightBool;
        if (left is Dictionary<string, object?> leftDict && right is Dictionary<string, object?> rightDict)
        {
            if (leftDict.Count != rightDict.Count) return false;
            foreach (var pair in leftDict)
            {
                if (!rightDict.TryGetValue(pair.Key, out var other)) return false;
                if (!DeepEqual(pair.Value, other)) return false;
            }
            return true;
        }
        if (left is IList leftList && right is IList rightList)
        {
            if (leftList.Count != rightList.Count) return false;
            for (var i = 0; i < leftList.Count; i++)
            {
                if (!DeepEqual(leftList[i], rightList[i])) return false;
            }
            return true;
        }
        if (IsNumeric(left) && IsNumeric(right))
        {
            return Convert.ToDouble(left, CultureInfo.InvariantCulture) == Convert.ToDouble(right, CultureInfo.InvariantCulture);
        }
        return left.Equals(right);
    }

    /// <summary>Detach one JSON-shaped value (never mutates the input).</summary>
    public static object? DeepClone(object? value)
    {
        switch (value)
        {
            case null or string or bool:
                return value;
            case int or long or double or float or decimal:
                return value;
            case IList list:
                return list.Cast<object?>().Select(DeepClone).ToList();
            case Dictionary<string, object?> dict:
                return dict.ToDictionary(pair => pair.Key, pair => DeepClone(pair.Value), StringComparer.Ordinal);
            default:
                return value;
        }
    }

    /// <summary>
    /// Layer <paramref name="over"/> onto <paramref name="under"/>: plain objects merge recursively,
    /// every other value (arrays included) replaces the lower layer wholesale.
    /// </summary>
    public static object? MergeLayers(object? under, object? over)
    {
        if (over is null) return under;
        if (under is not Dictionary<string, object?> underDict || over is not Dictionary<string, object?> overDict) return over;
        var merged = new Dictionary<string, object?>(underDict);
        foreach (var pair in overDict)
        {
            merged[pair.Key] = merged.ContainsKey(pair.Key) ? MergeLayers(merged[pair.Key], pair.Value) : pair.Value;
        }
        return merged;
    }

    /// <summary>
    /// Detach and validate one write input in a single walk before persistence: only JSON data
    /// (plain objects, arrays, strings, finite numbers, booleans, null) may reach a provider
    /// document.
    /// </summary>
    /// <param name="root">Plain-object write input.</param>
    /// <param name="reject">Builds the validation error from a value label and its $-rooted path.</param>
    /// <returns>The detached JSON-compatible clone.</returns>
    public static Dictionary<string, object?> CloneJsonShaped(Dictionary<string, object?> root, Func<string, string, Exception> reject)
    {
        var visiting = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? Clone(object? value, string path)
        {
            switch (value)
            {
                case null or string or bool:
                    return value;
                case int or long or double or float or decimal:
                    if (value is double doubleValue && !double.IsFinite(doubleValue)) throw reject("a non-finite number", path);
                    if (value is float floatValue && !float.IsFinite(floatValue)) throw reject("a non-finite number", path);
                    return value;
                case IList list:
                    if (!visiting.Add(list)) throw reject("a circular reference", path);
                    var entries = new List<object?>(list.Count);
                    for (var i = 0; i < list.Count; i++)
                    {
                        entries.Add(Clone(list[i], $"{path}[{i}]"));
                    }
                    visiting.Remove(list);
                    return entries;
                case Dictionary<string, object?> dict:
                    if (!visiting.Add(dict)) throw reject("a circular reference", path);
                    var output = new Dictionary<string, object?>();
                    foreach (var pair in dict)
                    {
                        output[pair.Key] = Clone(pair.Value, $"{path}.{pair.Key}");
                    }
                    visiting.Remove(dict);
                    return output;
                default:
                    throw reject(Describe(value), path);
            }
        }

        return (Dictionary<string, object?>)Clone(root, "$")!;
    }

    /// <summary>Convert one parsed JsonElement into the seam's JSON-value representation.</summary>
    public static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = FromElement(property.Value);
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer) ? integer : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static bool IsNumeric(object value) => value is int or long or double or float or decimal;

    private static string Describe(object? value)
    {
        if (value is null) return "undefined";
        return $"a {value.GetType().Name}";
    }
}

/// <summary>
/// Public JSON-value conversion for wire layers: one parsed JsonElement becomes the seam's
/// JSON-value representation (plain dictionaries, lists, and CLR scalars) that
/// <see cref="SettingsProvider.UpdateAsync"/>/<see cref="SettingsProvider.ReplaceAsync"/> accept.
/// </summary>
public static class SettingsWireValues
{
    /// <summary>Convert one parsed JsonElement into the settings seam's JSON-value representation.</summary>
    public static object? FromJsonElement(JsonElement element) => SettingsJson.FromElement(element);
}
