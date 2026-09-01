using Harness.Credentials;

namespace Harness.Credentials.Tests;

/// <summary>Masking and scrubbing behavior of <see cref="CredentialsRedaction"/>.</summary>
public static class RedactionTests
{
    public static void Mask_DoesNotLeakTheSecret()
    {
        const string secret = "s3cr3t";
        var mask = CredentialsRedaction.Mask(secret);
        Assert.Equal("s****t", mask);
        Assert.False(mask.Contains(secret, StringComparison.Ordinal));
    }

    public static void Mask_ShortValues_AreFullyMasked()
    {
        Assert.Equal("***", CredentialsRedaction.Mask("ab"));
        Assert.Equal("***", CredentialsRedaction.Mask("a"));
    }

    public static void Redact_RemovesSecretsFromText()
    {
        const string secret = "hunter2";
        var redacted = CredentialsRedaction.Redact("the api key is hunter2, keep it safe", new[] { secret });
        Assert.False(redacted.Contains(secret, StringComparison.Ordinal), redacted);
        Assert.True(redacted.Contains(CredentialsRedaction.Mask(secret), StringComparison.Ordinal), redacted);
    }

    public static void Redact_HandlesMultipleSecrets()
    {
        var redacted = CredentialsRedaction.Redact("a=aaa b=bbb", new[] { "aaa", "bbb" });
        Assert.False(redacted.Contains("aaa", StringComparison.Ordinal), redacted);
        Assert.False(redacted.Contains("bbb", StringComparison.Ordinal), redacted);
    }

    public static void Redact_SkipsEmptySecrets()
    {
        Assert.Equal("unchanged", CredentialsRedaction.Redact("unchanged", new[] { string.Empty }));
    }

    public static void ContainsSecret_DetectsSecrets()
    {
        Assert.True(CredentialsRedaction.ContainsSecret("key hunter2 here", new[] { "hunter2" }));
        Assert.False(CredentialsRedaction.ContainsSecret("key s****t here", new[] { "hunter2" }));
    }
}
