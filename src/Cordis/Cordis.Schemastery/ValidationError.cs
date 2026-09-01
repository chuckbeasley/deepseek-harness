using Harness.Cordis.Cosmokit;

namespace Harness.Cordis.Schemastery;

/// <summary>
/// A structured validation failure. The message carries a path prefix such as
/// <c>$.a.b[0]</c>; the <see cref="Path"/> property exposes the segments
/// (strings and ints) programmatically.
/// </summary>
public sealed class ValidationError : Exception
{
    /// <summary>The structured path segments (strings for object keys, ints for indices).</summary>
    public IReadOnlyList<object> Path { get; }

    /// <summary>The options active when the error was raised (carries the path).</summary>
    public SchemaOptions Options { get; }

    /// <summary>The message without the path prefix.</summary>
    public string RawMessage { get; }

    /// <summary>Creates a validation error and formats <paramref name="message"/> with the path prefix.</summary>
    public ValidationError(string message, SchemaOptions options)
        : base(FormatMessage(options.Path, message))
    {
        RawMessage = message;
        Options = options;
        Path = options.Path is null ? Array.Empty<object>() : options.Path.ToArray();
    }

    /// <summary>Returns <c>true</c> when <paramref name="error"/> is a <see cref="ValidationError"/>.</summary>
    public static bool Is(Exception? error) => error is ValidationError;

    /// <summary>Formats a path like JS <c>ValidationError</c>: <c>$</c>, <c>.key</c>, and <c>[index]</c> segments.</summary>
    internal static string FormatMessage(IReadOnlyList<object>? path, string message)
    {
        var prefix = "$";
        if (path is not null)
        {
            foreach (var segment in path)
            {
                if (segment is string text) prefix += "." + text;
                else if (segment is int index) prefix += "[" + index + "]";
                else prefix += "[Symbol(" + segment + ")]";
            }
        }
        return (prefix == "$" ? string.Empty : prefix + " ") + message;
    }
}
