namespace Dsh.Credentials;

/// <summary>
/// Redaction helpers for secret values. The local provider itself never emits values — this
/// utility is for consumers that build their own diagnostics from values they already hold: mask
/// a value for display, or scrub every known secret out of a text block before logging it.
/// </summary>
public static class CredentialsRedaction
{
    /// <summary>
    /// A deterministic display mask: the first and last character with the middle replaced by
    /// asterisks; a value of at most two characters masks entirely. The full secret never appears
    /// in the result.
    /// </summary>
    public static string Mask(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length <= 2) return "***";
        return secret[..1] + new string('*', secret.Length - 2) + secret[^1..];
    }

    /// <summary>
    /// Replace every occurrence of each known secret in <paramref name="text"/> with its
    /// <see cref="Mask"/>; empty secrets are skipped.
    /// </summary>
    public static string Redact(string text, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);
        var result = text;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret)) continue;
            result = result.Replace(secret, Mask(secret), StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Whether <paramref name="text"/> contains any of the known secrets verbatim.</summary>
    public static bool ContainsSecret(string text, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
