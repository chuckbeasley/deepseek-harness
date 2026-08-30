using System.Text.RegularExpressions;

namespace Dsh.Credentials;

/// <summary>
/// Service Definition of the credential-reference capability seam (ctx.credentials). Settings and
/// composition files carry <em>references</em> to secrets — environment-variable names — while
/// providers own the actual values and their storage. Consumers resolve a reference once per
/// operation, so a changed credential reaches the next operation without any plugin restart, and
/// configuration surfaces describe a reference without ever seeing its value. Port of
/// <c>@deepseek-ai/dsh-credentials</c> (the authorization half — grants, records, and the
/// <c>authorization</c> package — is a later wave and is not ported here).
/// </summary>
public static class CredentialNames
{
    private static readonly Regex RefPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Whether a raw string could name a reference at all (a POSIX shell identifier).</summary>
    public static bool IsCredentialRefName(string value)
        => value is not null && RefPattern.IsMatch(value);

    /// <summary>
    /// Validate a candidate reference. Throws loudly, naming only the key: a value never appears
    /// in a diagnostic.
    /// </summary>
    public static void ValidateReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!IsCredentialRefName(reference))
        {
            throw new ArgumentException($"credential ref \"{reference}\" must match [A-Za-z_][A-Za-z0-9_]*", nameof(reference));
        }
    }
}

/// <summary>One resolved credential value and the source layer that supplied it.</summary>
public sealed record ResolvedCredential(string Value, string Source);

/// <summary>
/// Source and writability facts for one reference, safe for configuration UIs — never the value.
/// The view has no slot a value could ride in.
/// </summary>
public sealed record CredentialInfo(bool Configured, string? Source = null, bool Writable = false);

/// <summary>
/// Thrown when a required credential is not configured. The message names the reference (the key)
/// and never a value — there is no value to quote, and the consumer maps this to its own
/// <c>MISSING_CREDENTIAL</c> code.
/// </summary>
public sealed class CredentialMissingError : Exception
{
    /// <summary>Create the error for the unconfigured <paramref name="reference"/>.</summary>
    public CredentialMissingError(string reference)
        : base($"credential \"{reference}\" is not configured")
    {
        Reference = reference;
    }

    /// <summary>The unconfigured reference.</summary>
    public string Reference { get; }
}

/// <summary>
/// The credential service (ctx.credentials). One seam-wide rule binds the reference half: an empty
/// value is absent everywhere — resolution skips it and description reports it unconfigured — so a
/// blank never masquerades as a configured secret.
/// </summary>
public interface ICredentialsService
{
    /// <summary>
    /// Resolve one reference to its current value. Resolution is per call: consumers re-resolve at
    /// each operation and must not cache across operations.
    /// </summary>
    /// <returns>the value and its source, or null while unconfigured.</returns>
    Task<ResolvedCredential?> ResolveAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>Resolve one reference or throw <see cref="CredentialMissingError"/> naming only the key.</summary>
    Task<ResolvedCredential> RequireAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describe one reference for configuration surfaces without exposing the value.
    /// </summary>
    /// <returns>configured state, supplying source, and writability.</returns>
    Task<CredentialInfo> DescribeAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably store one value in the provider-managed writable source. Rejects while a read-only
    /// source shadows the reference and rejects an empty value (use <see cref="UnsetAsync"/>).
    /// </summary>
    Task SetAsync(string reference, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove one reference from the provider-managed writable source; removing an absent
    /// reference is a no-op. Rejects while a read-only source shadows the reference.
    /// </summary>
    Task UnsetAsync(string reference, CancellationToken cancellationToken = default);
}
