using Harness.Cordis.Core;
using Harness.Credentials;

namespace Harness.Credentials.Tests;

/// <summary>One disposable temp directory per test, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-credentials-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone
        }
    }
}

/// <summary>
/// Layering, round-trip, and loud-failure behavior of <see cref="LocalCredentialsProvider"/>:
/// environment over managed file over project .env over user .env, with secret values never
/// appearing in diagnostics.
/// </summary>
public static class LocalCredentialsProviderTests
{
    private static Func<string, string?> Env(Dictionary<string, string>? values = null)
    {
        var snapshot = values ?? new Dictionary<string, string>();
        return name => snapshot.TryGetValue(name, out var value) ? value : null;
    }

    private static LocalCredentialsProvider Provider(
        Context ctx, string managedPath, string? projectPath = null, string? userPath = null, Dictionary<string, string>? env = null)
        => new(ctx, new LocalCredentialsConfig(ManagedPath: managedPath, ProjectEnvPath: projectPath, UserEnvPath: userPath), Env(env));

    public static void Environment_WinsOverEveryFileLayer()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(managed, "KEY=file-value\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, dir.File("project.env"), dir.File("user.env"), new Dictionary<string, string> { ["KEY"] = "env-value" });
        File.WriteAllText(dir.File("project.env"), "KEY=project-value\n");
        File.WriteAllText(dir.File("user.env"), "KEY=user-value\n");

        var resolved = provider.ResolveAsync("KEY").GetAwaiter().GetResult();
        Assert.NotNull(resolved);
        Assert.Equal("env-value", resolved!.Value);
        Assert.Equal("env", resolved.Source);
    }

    public static void ManagedFile_WinsOverProjectEnv()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(managed, "KEY=file-value\n");
        File.WriteAllText(dir.File("project.env"), "KEY=project-value\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, dir.File("project.env"), dir.File("user.env"));

        var resolved = provider.ResolveAsync("KEY").GetAwaiter().GetResult();
        Assert.NotNull(resolved);
        Assert.Equal("file-value", resolved!.Value);
        Assert.Equal("file", resolved.Source);
    }

    public static void ProjectEnv_IsTheFallback()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(dir.File("project.env"), "ONLY_KEY=project-value\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, dir.File("project.env"), dir.File("user.env"));

        var resolved = provider.ResolveAsync("ONLY_KEY").GetAwaiter().GetResult();
        Assert.NotNull(resolved);
        Assert.Equal("project-value", resolved!.Value);
        Assert.Equal("project-env", resolved.Source);
    }

    public static void ProjectEnv_RanksAboveUserEnv()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(dir.File("project.env"), "KEY=project-value\n");
        File.WriteAllText(dir.File("user.env"), "KEY=user-value\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, dir.File("project.env"), dir.File("user.env"));

        var resolved = provider.ResolveAsync("KEY").GetAwaiter().GetResult();
        Assert.Equal("project-value", resolved!.Value);
        Assert.Equal("project-env", resolved.Source);
    }

    public static void UserEnv_Used_WhenNoProjectLayer()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(dir.File("user.env"), "KEY=user-value\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, userPath: dir.File("user.env"));

        var resolved = provider.ResolveAsync("KEY").GetAwaiter().GetResult();
        Assert.Equal("user-value", resolved!.Value);
        Assert.Equal("user-env", resolved.Source);
    }

    public static void Unconfigured_ResolvesNull()
    {
        using var dir = new TempDir();
        using var ctx = new Context();
        var provider = Provider(ctx, dir.File("credentials.env"), dir.File("project.env"), dir.File("user.env"));
        Assert.Null(provider.ResolveAsync("NOT_SET_ANYWHERE").GetAwaiter().GetResult());
    }

    public static void MissingCredential_ErrorNamesTheKey_WithoutAnyValue()
    {
        using var dir = new TempDir();
        using var ctx = new Context();
        var provider = Provider(ctx, dir.File("credentials.env"));
        var error = Assert.ThrowsAny<CredentialMissingError>(() => provider.RequireAsync("DEEPSEEK_API_KEY"));
        Assert.Equal("DEEPSEEK_API_KEY", error.Reference);
        Assert.True(error.Message.Contains("DEEPSEEK_API_KEY", StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("hunter2", StringComparison.Ordinal), error.Message);
    }

    public static void Set_RoundTripsThroughTheManagedFile()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        provider.SetAsync("MY_KEY", "my-value").GetAwaiter().GetResult();

        var resolved = provider.ResolveAsync("MY_KEY").GetAwaiter().GetResult();
        Assert.NotNull(resolved);
        Assert.Equal("my-value", resolved!.Value);
        Assert.Equal("file", resolved.Source);
        Assert.Equal("MY_KEY=my-value\n", File.ReadAllText(managed));
    }

    public static void Set_QuotedValue_RoundTripsLosslessly()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        provider.SetAsync("SPACED", "hello world").GetAwaiter().GetResult();

        Assert.Equal("SPACED=\"hello world\"\n", File.ReadAllText(managed));
        var resolved = provider.ResolveAsync("SPACED").GetAwaiter().GetResult();
        Assert.Equal("hello world", resolved!.Value);
    }

    public static void Unset_RemovesTheEntry()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        provider.SetAsync("MY_KEY", "my-value").GetAwaiter().GetResult();
        provider.UnsetAsync("MY_KEY").GetAwaiter().GetResult();

        Assert.Null(provider.ResolveAsync("MY_KEY").GetAwaiter().GetResult());
        Assert.False(File.ReadAllText(managed).Contains("MY_KEY", StringComparison.Ordinal));
    }

    public static void Unset_AbsentReference_IsANoOp()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        provider.UnsetAsync("NOT_THERE").GetAwaiter().GetResult();
        Assert.False(File.Exists(managed));
    }

    public static void Set_EmptyValue_ThrowsNamingTheKey()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        var error = Assert.ThrowsAny<ArgumentException>(() => provider.SetAsync("MY_KEY", string.Empty));
        Assert.True(error.Message.Contains("MY_KEY", StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("secret", StringComparison.Ordinal), error.Message);
        Assert.False(File.Exists(managed));
    }

    public static void Set_ShadowedByEnvironment_ThrowsWithoutWriting()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, env: new Dictionary<string, string> { ["MY_KEY"] = "inherited" });
        var error = Assert.ThrowsAny<InvalidOperationException>(() => provider.SetAsync("MY_KEY", "stored"));
        Assert.True(error.Message.Contains("MY_KEY", StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("inherited", StringComparison.Ordinal), error.Message);
        Assert.False(File.Exists(managed));
    }

    public static void Set_PreservesUnrelatedEntries()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(managed, "EXISTING=keep-me\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        provider.SetAsync("NEW_KEY", "new-value").GetAwaiter().GetResult();

        var text = File.ReadAllText(managed);
        Assert.True(text.Contains("EXISTING=keep-me", StringComparison.Ordinal), text);
        Assert.True(text.Contains("NEW_KEY=new-value", StringComparison.Ordinal), text);
    }

    public static void CorruptManagedFile_FailsLoud_WithoutLeakingValues()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(managed, "API_KEY=supersecret123\nBROKEN=\"unterminated\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed);
        var error = Assert.ThrowsAny<CredentialsFileError>(() => provider.ResolveAsync("API_KEY"));
        Assert.True(error.Message.Contains(managed, StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("supersecret123", StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("unterminated\"\n", StringComparison.Ordinal), error.Message);
    }

    public static void MissingManagedFile_IsAnEmptyStore()
    {
        using var dir = new TempDir();
        using var ctx = new Context();
        var provider = Provider(ctx, dir.File("credentials.env"));
        Assert.Null(provider.ResolveAsync("ANY_KEY").GetAwaiter().GetResult());
    }

    public static void CorruptProjectEnv_FailsLoudToo()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(dir.File("project.env"), "KEY=good\n1BAD=leaked\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, dir.File("project.env"));
        var error = Assert.ThrowsAny<CredentialsFileError>(() => provider.ResolveAsync("KEY"));
        Assert.True(error.Message.Contains("1BAD", StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("leaked", StringComparison.Ordinal), error.Message);
    }

    public static void Describe_ReportsSourceAndWritability_WithoutValues()
    {
        using var dir = new TempDir();
        var managed = dir.File("credentials.env");
        File.WriteAllText(managed, "IN_FILE=file-value\n");
        File.WriteAllText(dir.File("project.env"), "IN_PROJECT=project-value\n");
        using var ctx = new Context();
        var provider = Provider(ctx, managed, dir.File("project.env"), env: new Dictionary<string, string> { ["IN_ENV"] = "env-value" });

        var fromEnv = provider.DescribeAsync("IN_ENV").GetAwaiter().GetResult();
        Assert.Equal(new CredentialInfo(true, "env", false), fromEnv);

        var fromFile = provider.DescribeAsync("IN_FILE").GetAwaiter().GetResult();
        Assert.Equal(new CredentialInfo(true, "file", true), fromFile);

        var fromProject = provider.DescribeAsync("IN_PROJECT").GetAwaiter().GetResult();
        Assert.Equal(new CredentialInfo(true, "project-env", true), fromProject);

        var unset = provider.DescribeAsync("NOWHERE").GetAwaiter().GetResult();
        Assert.Equal(new CredentialInfo(false, null, true), unset);
    }

    public static void InvalidReferenceName_IsRejectedLoudly()
    {
        using var dir = new TempDir();
        using var ctx = new Context();
        var provider = Provider(ctx, dir.File("credentials.env"));
        var error = Assert.ThrowsAny<ArgumentException>(() => provider.ResolveAsync("NOT A REF"));
        Assert.True(error.Message.Contains("NOT A REF", StringComparison.Ordinal), error.Message);
        Assert.ThrowsAny<ArgumentException>(() => provider.SetAsync("1BAD", "x"));
        Assert.ThrowsAny<ArgumentException>(() => provider.UnsetAsync("BAD-NAME"));
    }

    public static void Registered_UnderCredentialsKey()
    {
        using var dir = new TempDir();
        using var ctx = new Context();
        var provider = Provider(ctx, dir.File("credentials.env"));
        Assert.Same(provider, ctx.Get<LocalCredentialsProvider>("credentials"));
    }
}
