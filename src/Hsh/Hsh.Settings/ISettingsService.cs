using System.Text.RegularExpressions;
using Harness.Cordis.Schemastery;

namespace Harness.Settings;

/// <summary>Nominal id of one registered settings namespace (the TS SettingsNamespace brand).</summary>
public readonly record struct SettingsNamespace(string Value)
{
    /// <summary>Implicit unwrap to the raw namespace string.</summary>
    public static implicit operator string(SettingsNamespace ns) => ns.Value;

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>Namespace grammar and validation (the TS NAMESPACE_PATTERN).</summary>
public static class SettingsNamespaces
{
    private static readonly Regex Pattern = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>Whether a string is a valid lowercase hyphenated namespace.</summary>
    public static bool IsNamespace(string value) => Pattern.IsMatch(value);

    /// <summary>Parse and brand one namespace, failing loud on a shape violation.</summary>
    /// <exception cref="ArgumentException">when <paramref name="value"/> is not a lowercase hyphenated identifier.</exception>
    public static SettingsNamespace Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Pattern.IsMatch(value))
        {
            throw new ArgumentException($"settings namespace \"{value}\" must match {Pattern}", nameof(value));
        }
        return new SettingsNamespace(value);
    }
}

/// <summary>When a namespace's changes take effect for its owner.</summary>
public enum SettingsApplies
{
    /// <summary>Changes apply immediately.</summary>
    Live,

    /// <summary>Changes apply on the next restart.</summary>
    Restart,
}

/// <summary>Origin of one committed settings change.</summary>
public enum SettingsUpdateSource
{
    /// <summary>The change entered through an in-process update or replace.</summary>
    Update,

    /// <summary>The change entered through the provider's stored document.</summary>
    Provider,
}

/// <summary>One schema-declared secret slot inside a redacted namespace value.</summary>
/// <param name="Path">Path from the section root to the removed field (concrete dict keys and array indexes included).</param>
/// <param name="Set">Whether the slot currently holds a value; the value itself never rides.</param>
public sealed record SettingsSecret(string[] Path, bool Set);

/// <summary>Registration options beyond the namespace schema.</summary>
/// <param name="Base">Composition-layer values resolved below the user layer.</param>
/// <param name="Applies">Owner's effect timing, surfaced to configuration UIs; defaults to live.</param>
/// <param name="Validate">Reject a resolved section the owner could not act on, for constraints the schema cannot express.</param>
public sealed record SettingsRegisterOptions<T>(object? Base = null, SettingsApplies Applies = SettingsApplies.Live, Action<T>? Validate = null);

/// <summary>One registered namespace as surfaced to configuration surfaces.</summary>
/// <param name="Ns">The registered namespace.</param>
/// <param name="Schema">The namespace schema.</param>
/// <param name="Value">Current resolved value (redacted under <c>RedactSecrets</c>).</param>
/// <param name="Revision">Monotonic revision of the raw user section this descriptor was read at.</param>
/// <param name="Applies">Owner's declared effect timing.</param>
/// <param name="Base">Redacted composition base layer, when the registrant declared one.</param>
/// <param name="User">Redacted raw user section, when one exists and is well-formed.</param>
/// <param name="Secrets">Schema-declared secret positions; present only under <c>RedactSecrets</c>.</param>
public sealed record SettingsDescriptor(
    SettingsNamespace Ns,
    Schema Schema,
    object? Value,
    long Revision,
    SettingsApplies Applies,
    object? Base = null,
    object? User = null,
    IReadOnlyList<SettingsSecret>? Secrets = null);

/// <summary>
/// One path-addressed edit to a namespace's stored user section (port of the TS
/// <c>SettingsPathOp</c>). Path mutation exists for a caller holding an INCOMPLETE view of the
/// section — a configuration UI reads the redacted descriptor, which never received the
/// <c>role("secret")</c> fields, so it can name the field it means without restating the section:
/// a wholesale replace rebuilt from a redacted document silently deletes every secret the wire
/// never returned.
/// </summary>
/// <param name="Op"><c>set</c> writes <see cref="Value"/> at the path, creating intermediate
/// objects; <c>unset</c> removes it. The empty path addresses the section root.</param>
/// <param name="Path">Path from the section root to the edited field; every part is a concrete key.</param>
/// <param name="Value">The value for a <c>set</c> op; unused for <c>unset</c>.</param>
public sealed record SettingsPathOp(string Op, IReadOnlyList<string> Path, object? Value = null);

/// <summary>Options for <see cref="SettingsProvider.Describe"/>.</summary>
/// <param name="RedactSecrets">Strip <c>role("secret")</c> fields from value/base/user and enumerate them per descriptor; wire surfaces must redact.</param>
public sealed record SettingsDescribeOptions(bool RedactSecrets = false);

/// <summary>Owner-facing handle for one registered namespace.</summary>
/// <typeparam name="T">The resolved namespace value type.</typeparam>
public interface ISettingsScope<T>
{
    /// <summary>Current resolved value: schema defaults, then base, then the user layer.</summary>
    T Get();

    /// <summary>Observe committed changes to this namespace's resolved value; returns the disposer removing this observer.</summary>
    IDisposable Watch(Action<T, T> callback);

    /// <summary>Async variant of <see cref="Watch"/>; invocations of one callback run one at a time, in commit order.</summary>
    IDisposable WatchAsync(Func<T, T, Task> callback);

    /// <summary>Merge a partial patch into this namespace's user layer and persist it.</summary>
    Task UpdateAsync(object patch, long? expectedRevision = null);

    /// <summary>Replace this namespace's user section wholesale; absent keys re-inherit the base and schema defaults.</summary>
    Task ReplaceAsync(object section, long? expectedRevision = null);

    /// <summary>Apply ordered path-addressed edits to this namespace's user section; later ops observe earlier ones.</summary>
    Task MutateAsync(IReadOnlyList<SettingsPathOp> ops, long? expectedRevision = null);
}

/// <summary>Hooks a consumer hands to <see cref="SettingsProvider.InstallSection"/>.</summary>
/// <typeparam name="T">The resolved namespace value type.</typeparam>
public sealed class SettingsSectionHooks<T>
{
    /// <summary>Receive the active configuration source: the resolved settings scope while attached, the composition entry otherwise.</summary>
    public required Action<Func<T>> SetSource { get; init; }

    /// <summary>Re-judge anything derived from the source after an attach, a detach, or a committed change.</summary>
    public required Action OnChange { get; init; }

    /// <summary>Reject a resolved section this consumer could not act on, for constraints its schema cannot express.</summary>
    public Action<T>? Validate { get; init; }
}

/// <summary>
/// A write refused because the namespace moved since the caller read it. The serialized write
/// queue orders writes; it cannot tell a fresh writer from one holding a stale snapshot.
/// </summary>
public sealed class SettingsConflictError : Exception
{
    /// <summary>Stable machine code for wire layers mapping this to their own taxonomy.</summary>
    public string Code => "SETTINGS_CONFLICT";

    /// <summary>The revision the write expected.</summary>
    public long Expected { get; }

    /// <summary>The revision the namespace actually stands at.</summary>
    public long Actual { get; }

    /// <summary>Create the conflict error for one refused write.</summary>
    public SettingsConflictError(SettingsNamespace ns, long expected, long actual)
        : base($"settings namespace \"{ns}\" changed since it was read (expected revision {expected}, now {actual})")
    {
        Expected = expected;
        Actual = actual;
    }
}
