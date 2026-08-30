using Cordis.Schemastery;

namespace Dsh.Settings;

/// <summary>A value with every <c>role("secret")</c> field removed, plus the removal record.</summary>
/// <param name="Value">Detached copy of the input with secret fields absent.</param>
/// <param name="Secrets">Every reachable secret position: object properties always (even unset), dict entries and array items only where the value has them.</param>
public sealed record RedactedValue(object? Value, IReadOnlyList<SettingsSecret> Secrets);

/// <summary>
/// Structural secret redaction for settings values. <c>role("secret")</c> fields are removed from a
/// value before it crosses a wire or diagnostics boundary; a sidecar records each schema-declared
/// secret position and whether it currently holds a value.
/// </summary>
public static class SettingsRedaction
{
    /// <summary>
    /// Remove every <c>role("secret")</c> field a schema declares from a value. The walker follows
    /// <c>object</c>, <c>dict</c>, and <c>array</c> containers; the input is never mutated.
    /// </summary>
    /// <param name="schema">Live schema describing the value.</param>
    /// <param name="value">The value to strip; <c>null</c> yields an empty record with object-property secret slots still enumerated.</param>
    /// <returns>The stripped detached value and the ordered secret positions.</returns>
    public static RedactedValue RedactSecrets(Schema schema, object? value)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var secrets = new List<SettingsSecret>();
        var stripped = Walk(schema, value, new List<string>(), secrets);
        return new RedactedValue(stripped, secrets);
    }

    private static object? Walk(Schema? node, object? value, List<string> path, List<SettingsSecret> secrets)
    {
        if (node is null) return value;
        if (node.Meta.Role == "secret")
        {
            secrets.Add(new SettingsSecret(path.ToArray(), value is not null));
            return null;
        }
        switch (node.Type)
        {
            case "object":
            {
                var properties = node.PropertySchemas ?? new Dictionary<string, Schema>();
                var source = value as Dictionary<string, object?>;
                var rebuilt = new Dictionary<string, object?>();
                if (source is not null)
                {
                    foreach (var pair in source)
                    {
                        if (!properties.ContainsKey(pair.Key)) rebuilt[pair.Key] = pair.Value;
                    }
                }
                foreach (var pair in properties)
                {
                    var childPath = path.Append(pair.Key).ToList();
                    var entry = source is not null && source.TryGetValue(pair.Key, out var raw) ? raw : null;
                    var stripped = Walk(pair.Value, entry, childPath, secrets);
                    if (stripped is not null) rebuilt[pair.Key] = stripped;
                }
                return source is null && rebuilt.Count == 0 ? value : rebuilt;
            }
            case "dict":
            {
                if (value is not Dictionary<string, object?> entries) return value;
                var rebuilt = new Dictionary<string, object?>();
                foreach (var pair in entries)
                {
                    var childPath = path.Append(pair.Key).ToList();
                    var stripped = Walk(node.Inner, pair.Value, childPath, secrets);
                    if (stripped is not null) rebuilt[pair.Key] = stripped;
                }
                return rebuilt;
            }
            case "array":
            {
                if (value is not System.Collections.IList list) return value;
                var rebuilt = new List<object?>();
                for (var i = 0; i < list.Count; i++)
                {
                    var childPath = path.Append(i.ToString()).ToList();
                    rebuilt.Add(Walk(node.Inner, list[i], childPath, secrets));
                }
                return rebuilt;
            }
            default:
                // A secret reachable only through a union, intersection, or transform is returned
                // verbatim with nothing recording it (TS TODO settings-wire-redaction parity).
                return value;
        }
    }
}
