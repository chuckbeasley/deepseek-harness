using System.Globalization;
using System.Text.Json;
using Cordis.Cosmokit;

namespace Cordis.Schemastery;

/// <summary>Resolver callback used by built-in and <see cref="Schema.RegisterType"/> custom schema types.</summary>
/// <param name="data">The value being validated.</param>
/// <param name="schema">The schema node being resolved.</param>
/// <param name="options">Runtime validation options.</param>
/// <param name="strict">Whether the caller demands a strict (lossless) result.</param>
/// <returns>The normalized output, optionally with an adapted input.</returns>
public delegate ResolveResult SchemaResolver(object? data, Schema schema, SchemaOptions options, bool strict);

/// <summary>
/// A schema definition that validates a plain CLR value (dictionary, list, or
/// scalar) and returns normalized output, the C# port of the Schemastery
/// callable <c>Schema</c>. Nodes are immutable: builder methods return a new
/// schema sharing the same children with updated metadata, mirroring the JS
/// clone-on-build behavior.
/// </summary>
public sealed class Schema
{
    private static int _uidCounter;
    private static readonly Dictionary<string, SchemaResolver> Resolvers = new();

    static Schema()
    {
        SchemaResolvers.RegisterDefaults(Resolvers);
    }

    /// <summary>Creates an untyped schema node; use the static factories instead.</summary>
    public Schema()
    {
        Uid = _uidCounter++;
        Meta = new Meta();
    }

    /// <summary>Unique identifier for this node, mirroring the JS <c>uid</c>.</summary>
    public int Uid { get; }

    /// <summary>The schema type name used to dispatch validation, e.g. <c>object</c>.</summary>
    public string Type { get; internal set; } = "any";

    /// <summary>UI and validation metadata.</summary>
    public Meta Meta { get; internal set; }

    /// <summary>Key schema for <c>dict</c> nodes.</summary>
    public Schema? SKey { get; internal set; }

    /// <summary>Element schema for <c>array</c>/<c>dict</c>/<c>transform</c> nodes.</summary>
    public Schema? Inner { get; internal set; }

    /// <summary>Member schemas for <c>tuple</c>/<c>union</c>/<c>intersect</c> nodes.</summary>
    public List<Schema>? List { get; internal set; }

    /// <summary>Property schemas for <c>object</c> nodes.</summary>
    public Dictionary<string, Schema>? PropertySchemas { get; internal set; }

    /// <summary>Conversion callback for <c>transform</c> nodes.</summary>
    public Func<object?, SchemaOptions, object?>? Callback { get; internal set; }

    /// <summary>Deferred builder for <c>lazy</c> nodes.</summary>
    public Func<Schema>? Builder { get; internal set; }

    /// <summary>The constant value for <c>const</c> nodes.</summary>
    public object? Value { get; internal set; }

    /// <summary>Whether a <c>transform</c> preserves the adapted input.</summary>
    public bool Preserve { get; internal set; }

    /// <summary>Accepts any value without validation.</summary>
    public static Schema Any() => new() { Type = "any" };

    /// <summary>Accepts only nullable input.</summary>
    public static Schema Never() => new() { Type = "never" };

    /// <summary>Accepts exactly one constant value.</summary>
    public static Schema Const(object value) => new() { Type = "const", Value = value };

    /// <summary>Accepts strings, with optional metadata constraints added by builder methods.</summary>
    public static Schema String() => new() { Type = "string" };

    /// <summary>Accepts numbers, with optional range and step constraints.</summary>
    public static Schema Number() => new() { Type = "number" };

    /// <summary>Accepts non-negative integer numbers.</summary>
    public static Schema Natural() => Number().Step(1).Min(0);

    /// <summary>Accepts a number between 0 and 1 and marks it as a slider.</summary>
    public static Schema Percent() => Number().Step(0.01).Min(0).Max(1).Role("slider");

    /// <summary>Accepts booleans.</summary>
    public static Schema Boolean() => new() { Type = "boolean" };

    /// <summary>Accepts lists whose elements match <paramref name="inner"/>.</summary>
    public static Schema Array(Schema inner) => new()
    {
        Type = "array",
        Inner = inner,
        Meta = new Meta { Default = System.Array.Empty<object?>() },
    };

    /// <summary>Accepts plain objects whose values match <paramref name="inner"/>, with an optional key schema.</summary>
    public static Schema Dict(Schema inner, Schema? sKey = null) => new()
    {
        Type = "dict",
        Inner = inner,
        SKey = sKey ?? String(),
        Meta = new Meta { Default = new Dictionary<string, object?>() },
    };

    /// <summary>Accepts tuple lists where each index matches the corresponding schema.</summary>
    public static Schema Tuple(IReadOnlyList<Schema> list) => new()
    {
        Type = "tuple",
        List = list.ToList(),
        Meta = new Meta { Default = System.Array.Empty<object?>() },
    };

    /// <summary>Accepts plain objects whose declared properties match the schema dictionary.</summary>
    public static Schema Object(IDictionary<string, Schema> dict) => new()
    {
        Type = "object",
        PropertySchemas = new Dictionary<string, Schema>(dict),
        Meta = new Meta { Default = new Dictionary<string, object?>() },
    };

    /// <summary>Accepts values matching at least one schema in <paramref name="list"/>.</summary>
    public static Schema Union(IReadOnlyList<Schema> list) => new() { Type = "union", List = list.ToList() };

    /// <summary>Accepts values matching every schema in <paramref name="list"/>, merging object outputs.</summary>
    public static Schema Intersect(IReadOnlyList<Schema> list) => new() { Type = "intersect", List = list.ToList() };

    /// <summary>Validates with <paramref name="inner"/>, then converts the result with <paramref name="callback"/>.</summary>
    public static Schema Transform(Schema inner, Func<object?, SchemaOptions, object?> callback, bool preserve = false) => new()
    {
        Type = "transform",
        Inner = inner,
        Callback = callback,
        Preserve = preserve,
    };

    /// <summary>Defers construction of a recursive schema until validation.</summary>
    public static Schema Lazy(Func<Schema> builder) => new() { Type = "lazy", Builder = builder };

    /// <summary>
    /// Infers a schema from a value: nullable becomes <see cref="Any"/>, a
    /// primitive becomes a required <see cref="Const"/>, and a schema returns
    /// itself. Constructor objects (JS <c>String</c>/<c>Number</c>/
    /// <c>Boolean</c>) have no C# equivalent and are rejected.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot be inferred.</exception>
    public static Schema From(object? source)
    {
        if (Misc.IsNullable(source)) return Any();
        if (source is string || Misc.IsNumeric(source) || source is bool) return Const(source!).Required();
        if (source is Schema schema) return schema;
        throw new ArgumentException($"cannot infer schema from {source}");
    }

    /// <summary>Registers a resolver for a custom schema <paramref name="type"/> (the <c>Schema.extend</c> equivalent).</summary>
    public static void RegisterType(string type, SchemaResolver resolver)
    {
        lock (Resolvers)
        {
            Resolvers[type] = resolver;
        }
    }

    /// <summary>
    /// Resolves <paramref name="data"/> against <paramref name="schema"/>,
    /// applying required/default semantics and dispatching to the type
    /// resolver. Throws <see cref="ValidationError"/> on failure unless the
    /// schema is <c>loose</c>, in which case the default is returned.
    /// </summary>
    public static ResolveResult Resolve(object? data, Schema? schema, SchemaOptions? options = null, bool strict = false)
    {
        options ??= new SchemaOptions();
        if (schema is null) return ResolveResult.Of(data);
        if (options.Ignore?.Invoke(data, schema) == true) return ResolveResult.Of(data);

        if (Misc.IsNullable(data) && schema.Type != "lazy")
        {
            if (schema.Meta.Required) throw new ValidationError("missing required value", options);
            var current = schema;
            var fallback = schema.Meta.Default;
            while (current.Type == "intersect" && Misc.IsNullable(fallback) && current.List is { Count: > 0 })
            {
                current = current.List[0];
                fallback = current.Meta.Default;
            }
            if (Misc.IsNullable(fallback)) return ResolveResult.Of(data);
            data = Deep.Clone(fallback);
        }

        SchemaResolver? callback;
        lock (Resolvers)
        {
            Resolvers.TryGetValue(schema.Type, out callback);
        }
        if (callback is null) throw new ValidationError($"unsupported type \"{schema.Type}\"", options);

        try
        {
            return callback(data, schema, options, strict);
        }
        catch (Exception) when (schema.Meta.Loose)
        {
            return ResolveResult.Of(schema.Meta.Default);
        }
    }

    /// <summary>Validates <paramref name="data"/> and returns the normalized output, throwing on failure.</summary>
    public object? Validate(object? data, SchemaOptions? options = null) => Resolve(data, this, options).Value;

    /// <summary>Validates <paramref name="data"/> and returns a structured result instead of throwing.</summary>
    public SchemaValidationResult TryValidate(object? data, SchemaOptions? options = null)
    {
        try
        {
            return SchemaValidationResult.Success(Validate(data, options));
        }
        catch (ValidationError error)
        {
            return SchemaValidationResult.Failure(new[] { error });
        }
    }

    /// <summary>Marks nullable input as invalid unless a default supplies a fallback.</summary>
    public Schema Required(bool value = true) => WithMeta(meta => meta.Required = value);

    /// <summary>Hides this node from UI renderers.</summary>
    public Schema Hidden(bool value = true) => WithMeta(meta => meta.Hidden = value);

    /// <summary>Returns the default value instead of throwing when validation fails.</summary>
    public Schema Loose(bool value = true) => WithMeta(meta => meta.Loose = value);

    /// <summary>Marks this node as disabled for form UIs.</summary>
    public Schema Disabled(bool value = true) => WithMeta(meta => meta.Disabled = value);

    /// <summary>Requests collapsed rendering for nested form UIs.</summary>
    public Schema Collapse(bool value = true) => WithMeta(meta => meta.Collapse = value);

    /// <summary>Attaches a renderer role and optional role-specific metadata.</summary>
    public Schema Role(string role, object? extra = null) => WithMeta(meta => { meta.Role = role; meta.Extra = extra; });

    /// <summary>Attaches an external documentation link.</summary>
    public Schema Link(string link) => WithMeta(meta => meta.Link = link);

    /// <summary>Sets the fallback value used for nullable input.</summary>
    public Schema Default(object? value) => WithMeta(meta => meta.Default = value);

    /// <summary>Attaches an auxiliary comment for documentation or form UIs.</summary>
    public Schema Comment(string comment) => WithMeta(meta => meta.Comment = comment);

    /// <summary>Attaches a localized or plain description for documentation or form UIs.</summary>
    public Schema Description(string description) => WithMeta(meta => meta.Description = description);

    /// <summary>Requires strings to match a regular expression.</summary>
    public Schema Pattern(System.Text.RegularExpressions.Regex regexp)
    {
        var flags = string.Empty;
        if ((regexp.Options & System.Text.RegularExpressions.RegexOptions.IgnoreCase) != 0) flags += "i";
        if ((regexp.Options & System.Text.RegularExpressions.RegexOptions.Multiline) != 0) flags += "m";
        if ((regexp.Options & System.Text.RegularExpressions.RegexOptions.Singleline) != 0) flags += "s";
        return WithMeta(meta => meta.Pattern = (regexp.ToString(), flags));
    }

    /// <summary>Sets an inclusive maximum for numbers or collection lengths.</summary>
    public Schema Max(double value) => WithMeta(meta => meta.Max = value);

    /// <summary>Sets an inclusive minimum for numbers or collection lengths.</summary>
    public Schema Min(double value) => WithMeta(meta => meta.Min = value);

    /// <summary>Sets the numeric increment constraint.</summary>
    public Schema Step(double value) => WithMeta(meta => meta.Step = value);

    /// <summary>Adds a deprecated badge to this node.</summary>
    public Schema Deprecated()
    {
        return WithMeta(meta =>
        {
            meta.Badges ??= new List<Badge>();
            meta.Badges.Add(new Badge("deprecated", "danger"));
        });
    }

    /// <summary>Adds an experimental badge to this node.</summary>
    public Schema Experimental()
    {
        return WithMeta(meta =>
        {
            meta.Badges ??= new List<Badge>();
            meta.Badges.Add(new Badge("experimental", "warning"));
        });
    }

    /// <summary>Adds or replaces an object property schema; returns a new node.</summary>
    public Schema Set(string key, Schema value)
    {
        var clone = Clone();
        if (clone.PropertySchemas is null) throw new InvalidOperationException("Set requires an object schema");
        clone.PropertySchemas[key] = value;
        return clone;
    }

    /// <summary>Appends a tuple, union, or intersection member schema; returns a new node.</summary>
    public Schema Push(Schema value)
    {
        var clone = Clone();
        if (clone.List is null) throw new InvalidOperationException("Push requires a tuple, union, or intersect schema");
        clone.List.Add(value);
        return clone;
    }

    /// <summary>
    /// Attaches arbitrary metadata consumed by form renderers and downstream
    /// tools; returns a new node.
    /// </summary>
    public Schema Extra(string key, object? value)
    {
        return WithMeta(meta =>
        {
            switch (key)
            {
                case "default": meta.Default = value; break;
                case "required": meta.Required = value is true; break;
                case "disabled": meta.Disabled = value is true; break;
                case "collapse": meta.Collapse = value is true; break;
                case "hidden": meta.Hidden = value is true; break;
                case "loose": meta.Loose = value is true; break;
                case "role": meta.Role = value as string; break;
                case "extra": meta.Extra = value; break;
                case "link": meta.Link = value as string; break;
                case "description": meta.Description = value; break;
                case "comment": meta.Comment = value as string; break;
                case "max": meta.Max = ToDouble(value); break;
                case "min": meta.Min = ToDouble(value); break;
                case "step": meta.Step = ToDouble(value); break;
                default: throw new ArgumentException($"unknown metadata key \"{key}\"", nameof(key));
            }
        });
    }

    /// <summary>
    /// Removes values equal to schema defaults from normalized output; returns
    /// <c>null</c> when the whole value matches the default.
    /// </summary>
    public object? Simplify(object? value)
    {
        if (Deep.DeepEqual(value, Meta.Default, Type == "dict")) return null;
        if (Misc.IsNullable(value)) return value;
        if (Type is "object" or "dict")
        {
            var plain = Misc.ToPlainDictionary(value);
            var result = new Dictionary<string, object?>();
            if (plain is not null)
            {
                foreach (var pair in plain)
                {
                    var inner = Type == "object" ? PropertySchemas?.GetValueOrDefault(pair.Key) : Inner;
                    var item = inner?.Simplify(pair.Value);
                    if (Type == "dict" || !Misc.IsNullable(item)) result[pair.Key] = item;
                }
            }
            if (Deep.DeepEqual(result, Meta.Default, Type == "dict")) return null;
            return result;
        }
        if (Type is "array" or "tuple")
        {
            var result = new List<object?>();
            if (value is System.Collections.IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var inner = Type == "array" ? Inner : List?[i];
                    result.Add(inner is null ? list[i] : inner.Simplify(list[i]));
                }
            }
            return result;
        }
        if (Type == "intersect")
        {
            var result = new Dictionary<string, object?>();
            if (List is not null)
            {
                foreach (var inner in List)
                {
                    if (inner.Simplify(value) is System.Collections.IDictionary dict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in dict)
                        {
                            result[entry.Key.ToString() ?? string.Empty] = entry.Value;
                        }
                    }
                }
            }
            return result;
        }
        if (Type == "union" && List is not null)
        {
            foreach (var inner in List)
            {
                try
                {
                    Resolve(value, inner, new SchemaOptions());
                    return inner.Simplify(value);
                }
                catch (ValidationError)
                {
                    // Try the next union member.
                }
            }
        }
        return value;
    }

    /// <summary>Formats this schema as a compact TypeScript-like type string.</summary>
    public string ToString(bool inline) => SchemaResolvers.Format(this, inline) ?? $"Schema<{Type}>";

    /// <inheritdoc/>
    public override string ToString() => ToString(false);

    /// <summary>
    /// Serialize this schema as the wire envelope the TS <c>toJSON()</c> produces:
    /// <c>{ uid, refs }</c> with every reachable node once under its uid and child references
    /// riding as uid numbers, so shared and recursive nodes survive the round trip. Callables
    /// (callback, builder) never serialize.
    /// </summary>
    public JsonElement ToJson()
    {
        var refs = new Dictionary<string, object?>();
        Collect(this, refs, new HashSet<Schema>(ReferenceEqualityComparer.Instance));
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["uid"] = Uid,
            ["refs"] = refs,
        });
    }

    private static void Collect(Schema schema, Dictionary<string, object?> refs, HashSet<Schema> visited)
    {
        if (!visited.Add(schema)) return;
        refs[schema.Uid.ToString(CultureInfo.InvariantCulture)] = NodeJson(schema);
        if (schema.PropertySchemas is not null)
        {
            foreach (var child in schema.PropertySchemas.Values) Collect(child, refs, visited);
        }
        if (schema.List is not null)
        {
            foreach (var child in schema.List) Collect(child, refs, visited);
        }
        if (schema.Inner is not null) Collect(schema.Inner, refs, visited);
        if (schema.SKey is not null) Collect(schema.SKey, refs, visited);
    }

    private static Dictionary<string, object?> NodeJson(Schema schema)
    {
        var node = new Dictionary<string, object?>
        {
            ["uid"] = schema.Uid,
            ["type"] = schema.Type,
            ["meta"] = MetaJson(schema.Meta),
        };
        if (schema.PropertySchemas is not null)
        {
            node["dict"] = schema.PropertySchemas.ToDictionary(
                pair => pair.Key, pair => (object)pair.Value.Uid, StringComparer.Ordinal);
        }
        if (schema.List is not null)
        {
            node["list"] = schema.List.Select(child => (object)child.Uid).ToList();
        }
        if (schema.Inner is not null) node["inner"] = schema.Inner.Uid;
        if (schema.SKey is not null) node["sKey"] = schema.SKey.Uid;
        if (schema.Value is not null) node["value"] = schema.Value;
        if (schema.Preserve) node["preserve"] = true;
        return node;
    }

    private static Dictionary<string, object?> MetaJson(Meta meta)
    {
        var result = new Dictionary<string, object?>();
        if (meta.Default is not null) result["default"] = meta.Default;
        if (meta.Required) result["required"] = true;
        if (meta.Disabled) result["disabled"] = true;
        if (meta.Collapse) result["collapse"] = true;
        if (meta.Hidden) result["hidden"] = true;
        if (meta.Loose) result["loose"] = true;
        if (meta.Role is not null) result["role"] = meta.Role;
        if (meta.Extra is not null) result["extra"] = meta.Extra;
        if (meta.Link is not null) result["link"] = meta.Link;
        if (meta.Description is not null) result["description"] = meta.Description;
        if (meta.Comment is not null) result["comment"] = meta.Comment;
        if (meta.Pattern is { } pattern)
        {
            result["pattern"] = pattern.Flags.Length == 0
                ? new Dictionary<string, object?> { ["source"] = pattern.Source }
                : new Dictionary<string, object?> { ["source"] = pattern.Source, ["flags"] = pattern.Flags };
        }
        if (meta.Max is double maxValue) result["max"] = maxValue;
        if (meta.Min is double minValue) result["min"] = minValue;
        if (meta.Step is double stepValue) result["step"] = stepValue;
        if (meta.Badges is { Count: > 0 } badges)
        {
            result["badges"] = badges
                .Select(badge => (object)new Dictionary<string, object?>
                {
                    ["text"] = badge.Text,
                    ["type"] = badge.Type,
                })
                .ToList();
        }
        return result;
    }

    private Schema Clone()
    {
        return new Schema
        {
            Type = Type,
            Meta = Meta,
            SKey = SKey,
            Inner = Inner,
            List = List,
            PropertySchemas = PropertySchemas,
            Callback = Callback,
            Builder = Builder,
            Value = Value,
            Preserve = Preserve,
        };
    }

    private Schema WithMeta(Action<Meta> update)
    {
        var clone = Clone();
        var meta = new Meta(Meta);
        update(meta);
        clone.Meta = meta;
        return clone;
    }

    private static double? ToDouble(object? value)
    {
        if (value is null) return null;
        if (value is double d) return d;
        if (value is int i) return i;
        if (Misc.IsNumeric(value)) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (value is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new ArgumentException($"cannot convert {value} to a number", nameof(value));
    }
}




