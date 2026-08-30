using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cordis.Cosmokit;

namespace Cordis.Schemastery;

/// <summary>
/// Per-type validation logic and type-string formatters, mirroring the
/// <c>Schema.extend</c> registrations and <c>formatters</c> table of the
/// Schemastery port.
/// </summary>
internal static class SchemaResolvers
{
    private static readonly Regex DecimalPattern = new(@"^\d+\.\d+$", RegexOptions.Compiled);

    internal static void RegisterDefaults(Dictionary<string, SchemaResolver> resolvers)
    {
        resolvers["lazy"] = ResolveLazy;
        resolvers["any"] = (data, _, _, _) => ResolveResult.Of(data);
        resolvers["never"] = (data, _, options, _) => throw new ValidationError($"expected nullable but got {Show(data)}", options);
        resolvers["const"] = ResolveConst;
        resolvers["string"] = ResolveString;
        resolvers["number"] = ResolveNumber;
        resolvers["boolean"] = (data, _, options, _) => data is bool
            ? ResolveResult.Of(data)
            : throw new ValidationError($"expected boolean but got {Show(data)}", options);
        resolvers["array"] = ResolveArray;
        resolvers["dict"] = ResolveDict;
        resolvers["tuple"] = ResolveTuple;
        resolvers["object"] = ResolveObject;
        resolvers["union"] = ResolveUnion;
        resolvers["intersect"] = ResolveIntersect;
        resolvers["transform"] = ResolveTransform;
    }

    private static ResolveResult ResolveLazy(object? data, Schema schema, SchemaOptions options, bool strict)
    {
        if (schema.Inner is null)
        {
            var built = schema.Builder!();
            built.Meta = Meta.Merge(schema.Meta, built.Meta);
            schema.Inner = built;
        }
        return Schema.Resolve(data, schema.Inner, options, strict);
    }

    private static ResolveResult ResolveConst(object? data, Schema schema, SchemaOptions options, bool _)
    {
        if (Deep.DeepEqual(data, schema.Value)) return ResolveResult.Of(schema.Value);
        throw new ValidationError($"expected {Show(schema.Value)} but got {Show(data)}", options);
    }

    private static ResolveResult ResolveString(object? data, Schema schema, SchemaOptions options, bool _)
    {
        if (data is not string text) throw new ValidationError($"expected string but got {Show(data)}", options);
        if (schema.Meta.Pattern is { } pattern)
        {
            var regexp = new Regex(pattern.Source, RegexOptionsFromFlags(pattern.Flags));
            if (!regexp.IsMatch(text))
            {
                throw new ValidationError($"expect string to match regexp /{pattern.Source}/{pattern.Flags}", options);
            }
        }
        CheckWithinRange(text.Length, schema.Meta, "string length", options);
        return ResolveResult.Of(data);
    }

    private static ResolveResult ResolveNumber(object? data, Schema schema, SchemaOptions options, bool _)
    {
        if (!Misc.IsNumeric(data)) throw new ValidationError($"expected number but got {Show(data)}", options);
        var value = Convert.ToDouble(data, CultureInfo.InvariantCulture);
        CheckWithinRange(value, schema.Meta, "number", options);
        if (schema.Meta.Step is { } step && !IsMultipleOf(value, schema.Meta.Min ?? 0, step))
        {
            throw new ValidationError($"expected number multiple of {step} but got {Show(data)}", options);
        }
        return ResolveResult.Of(data);
    }

    private static ResolveResult ResolveArray(object? data, Schema schema, SchemaOptions options, bool _)
    {
        if (data is not IList list) throw new ValidationError($"expected array but got {Show(data)}", options);
        var inner = schema.Inner!;
        CheckWithinRange(list.Count, schema.Meta, "array length", options, skipMin: !Misc.IsNullable(inner.Meta.Default));
        var result = new object?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            result[i] = Property(list, i, inner, options);
        }
        return ResolveResult.Of(result);
    }

    private static ResolveResult ResolveDict(object? data, Schema schema, SchemaOptions options, bool strict)
    {
        var plain = Misc.ToPlainDictionary(data);
        if (plain is null) throw new ValidationError($"expected object but got {Show(data)}", options);
        var sKey = schema.SKey!;
        var inner = schema.Inner!;
        var result = new Dictionary<string, object?>();
        foreach (var pair in plain.ToList())
        {
            string resolvedKey;
            try
            {
                resolvedKey = Schema.Resolve(pair.Key, sKey, options).Value as string ?? pair.Key;
            }
            catch (Exception) when (strict)
            {
                continue;
            }
            result[resolvedKey] = Property(plain, pair.Key, inner, options);
            if (plain is IDictionary<string, object?> editable && editable.TryGetValue(pair.Key, out var raw))
            {
                editable[resolvedKey] = raw;
                if (resolvedKey != pair.Key) editable.Remove(pair.Key);
            }
        }
        return ResolveResult.Of(result);
    }

    private static ResolveResult ResolveTuple(object? data, Schema schema, SchemaOptions options, bool strict)
    {
        if (data is not IList list) throw new ValidationError($"expected array but got {Show(data)}", options);
        var schemas = schema.List!;
        var result = new List<object?>(schemas.Count);
        for (var i = 0; i < schemas.Count; i++)
        {
            result.Add(Property(list, i, schemas[i], options));
        }
        if (!strict)
        {
            for (var i = schemas.Count; i < list.Count; i++)
            {
                result.Add(list[i]);
            }
        }
        return ResolveResult.Of(result);
    }

    private static ResolveResult ResolveObject(object? data, Schema schema, SchemaOptions options, bool strict)
    {
        var plain = Misc.ToPlainDictionary(data);
        if (plain is null) throw new ValidationError($"expected object but got {Show(data)}", options);
        var result = new Dictionary<string, object?>();
        foreach (var pair in schema.PropertySchemas!)
        {
            var value = Property(plain, pair.Key, pair.Value, options);
            if (!Misc.IsNullable(value) || plain.ContainsKey(pair.Key)) result[pair.Key] = value;
        }
        if (!strict)
        {
            foreach (var pair in plain)
            {
                if (!result.ContainsKey(pair.Key)) result[pair.Key] = pair.Value;
            }
        }
        return ResolveResult.Of(result);
    }

    private static ResolveResult ResolveUnion(object? data, Schema schema, SchemaOptions options, bool strict)
    {
        foreach (var inner in schema.List!)
        {
            try
            {
                return Schema.Resolve(data, inner, options, strict);
            }
            catch (Exception)
            {
                // Try the next member; the final failure carries the union's type string.
            }
        }
        throw new ValidationError($"expected {schema.ToString()} but got {Json(data)}", options);
    }

    private static ResolveResult ResolveIntersect(object? data, Schema schema, SchemaOptions options, bool strict)
    {
        var list = schema.List!;
        if (list.Count == 0) return ResolveResult.Of(data);
        object? result = null;
        foreach (var inner in list)
        {
            var value = Schema.Resolve(data, inner, options, true).Value;
            if (Misc.IsNullable(value)) continue;
            if (result is null)
            {
                result = value;
                continue;
            }
            if (!SameValueFamily(result!, value))
            {
                throw new ValidationError($"expected {schema.ToString()} but got {Json(data)}", options);
            }
            if (result is IDictionary target && value is IDictionary source)
            {
                foreach (DictionaryEntry entry in source)
                {
                    if (!target.Contains(entry.Key)) target[entry.Key] = entry.Value;
                }
            }
            else if (result is IList targetList && value is IList sourceList)
            {
                var mergeList = targetList is Array ? new List<object?>(targetList.Cast<object?>()) : targetList;
                for (var i = mergeList.Count; i < sourceList.Count; i++)
                {
                    mergeList.Add(sourceList[i]);
                }
                result = mergeList;
            }
            else if (!Deep.DeepEqual(result, value))
            {
                throw new ValidationError($"expected {schema.ToString()} but got {Json(data)}", options);
            }
        }
        if (!strict && data is IDictionary input)
        {
            if (result is null) result = new Dictionary<string, object?>();
            if (result is IDictionary mergeTarget)
            {
                foreach (DictionaryEntry entry in input)
                {
                    if (!mergeTarget.Contains(entry.Key)) mergeTarget[entry.Key] = entry.Value;
                }
            }
        }
        return ResolveResult.Of(result);
    }

    private static ResolveResult ResolveTransform(object? data, Schema schema, SchemaOptions options, bool _)
    {
        var resolved = Schema.Resolve(data, schema.Inner!, options, true);
        var result = resolved.Value;
        var adapted = resolved.HasAdapted ? resolved.Adapted : data;
        var callback = schema.Callback!;
        if (schema.Preserve) return ResolveResult.Of(callback(result, options));
        return ResolveResult.OfAdapted(callback(result, options), callback(adapted, options));
    }

    /// <summary>Resolves one child value, extending the error path, with optional autofix removal.</summary>
    internal static object? Property(object container, object key, Schema schema, SchemaOptions options)
    {
        try
        {
            var resolved = Schema.Resolve(GetValue(container, key), schema, options.WithPath(key));
            if (resolved.HasAdapted) SetValue(container, key, resolved.Adapted);
            return resolved.Value;
        }
        catch (Exception) when (options.Autofix)
        {
            RemoveValue(container, key);
            return schema.Meta.Default;
        }
    }

    private static object? GetValue(object container, object key)
    {
        if (container is IList list && key is int index)
        {
            return index >= 0 && index < list.Count ? list[index] : null;
        }
        if (container is IDictionary dict)
        {
            return dict.Contains(key) ? dict[key] : null;
        }
        return null;
    }

    private static void SetValue(object container, object key, object? value)
    {
        if (container is IList list && key is int index && index >= 0 && index < list.Count)
        {
            list[index] = value;
        }
        else if (container is IDictionary dict && key is string text)
        {
            dict[text] = value;
        }
    }

    private static void RemoveValue(object container, object key)
    {
        if (container is IList list && key is int index && index >= 0 && index < list.Count)
        {
            list[index] = null; // CLR lists have no holes; null approximates the JS `delete` outcome.
        }
        else if (container is IDictionary dict)
        {
            dict.Remove(key);
        }
    }

    private static void CheckWithinRange(double data, Meta meta, string description, SchemaOptions options, bool skipMin = false)
    {
        var max = meta.Max ?? double.PositiveInfinity;
        var min = meta.Min ?? double.NegativeInfinity;
        if (data > max) throw new ValidationError($"expected {description} <= {max} but got {data}", options);
        if (data < min && !skipMin) throw new ValidationError($"expected {description} >= {min} but got {data}", options);
    }

    private static bool IsMultipleOf(double data, double min, double step)
    {
        step = Math.Abs(step);
        var stepText = step.ToString("R", CultureInfo.InvariantCulture);
        if (!DecimalPattern.IsMatch(stepText))
        {
            return (data - min) % step == 0;
        }
        var digits = stepText.Length - stepText.IndexOf('.') - 1;
        return Math.Abs(DecimalShift(data, digits) - DecimalShift(min, digits)) % DecimalShift(step, digits) == 0;
    }

    private static double DecimalShift(double data, int digits)
    {
        var text = data.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('e')) return data * Math.Pow(10, digits);
        var index = text.IndexOf('.');
        if (index == -1) return data * Math.Pow(10, digits);
        var fraction = text[(index + 1)..];
        var integer = text[..index];
        if (fraction.Length <= digits)
        {
            return double.Parse(integer + fraction.PadRight(digits, '0'), CultureInfo.InvariantCulture);
        }
        return double.Parse(integer + fraction[..digits] + "." + fraction[digits..], CultureInfo.InvariantCulture);
    }

    private static bool SameValueFamily(object a, object b)
        => (a is IDictionary && b is IDictionary)
        || (a is IList && b is IList)
        || (a is DateTime && b is DateTime)
        || (Misc.IsNumeric(a) && Misc.IsNumeric(b))
        || (a is string && b is string)
        || (a is bool && b is bool);

    private static RegexOptions RegexOptionsFromFlags(string flags)
    {
        var options = RegexOptions.None;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        return options;
    }

    private static string Show(object? value) => value?.ToString() ?? "null";

    private static string Json(object? value)
    {
        if (value is null) return "null";
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            return Show(value);
        }
    }

    /// <summary>Formats a schema as a TypeScript-like type string; returns <c>null</c> for unformatted types.</summary>
    internal static string? Format(Schema schema, bool inline)
    {
        return schema.Type switch
        {
            "any" => "any",
            "never" => "never",
            "const" => schema.Value is string text ? Json(text) : Show(schema.Value),
            "string" => "string",
            "number" => "number",
            "boolean" => "boolean",
            "bitset" => "bitset",
            "function" => "function",
            "array" => $"{schema.Inner!.ToString(true)}[]",
            "dict" => $"{{ [key: {schema.SKey!.ToString()}]: {schema.Inner!.ToString()} }}",
            "tuple" => $"[{string.Join(", ", schema.List!.Select(item => item.ToString()))}]",
            "object" => FormatObject(schema),
            "union" => inline
                ? $"({string.Join(" | ", schema.List!.Select(item => item.ToString()))})"
                : string.Join(" | ", schema.List!.Select(item => item.ToString())),
            "intersect" => string.Join(" & ", schema.List!.Select(item => item.ToString(true))),
            "transform" => schema.Inner!.ToString(inline),
            "lazy" => schema.Inner is null ? null : schema.Inner.ToString(inline),
            _ => null,
        };
    }

    private static string FormatObject(Schema schema)
    {
        var dict = schema.PropertySchemas!;
        if (dict.Count == 0) return "{}";
        return "{ " + string.Join(", ", dict.Select(pair =>
            $"{pair.Key}{(pair.Value.Meta.Required ? string.Empty : "?")}: {pair.Value.ToString()}")) + " }";
    }
}




