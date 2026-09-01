using Harness.Cordis.Core;
using Harness.Credentials;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The credentials remotes: the batched describe facts, the grammar and batch bounds, and the
/// set/unset writes with their credential/rejected refusal, all through the registry so the wire
/// codes ride the responses. No assertion here ever prints a secret value.
/// </summary>
public static class CredentialsRemotesTests
{
    public static void Describe_ReturnsPerRefFacts()
    {
        var root = TempRoot();
        var (ctx, registry, credentials) = Boot(root, new Dictionary<string, string> { ["API_KEY"] = "sk-env" });
        try
        {
            credentials.SetAsync("MANAGED", "file-value").GetAwaiter().GetResult();
            var args = JsonSerializer.SerializeToElement(new { refs = new[] { "API_KEY", "MANAGED", "GHOST" } });
            var response = registry.InvokeAsync(new RpcRequest("credentials/describe", args)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the batch describe must succeed");
            var entries = response.Result!.Value;

            var env = entries.GetProperty("API_KEY");
            Assert.True(env.GetProperty("configured").GetBoolean(), "the environment value is configured");
            Assert.Equal("env", env.GetProperty("source").GetString());
            Assert.False(env.GetProperty("writable").GetBoolean(), "the inherited environment is read-only");

            var managed = entries.GetProperty("MANAGED");
            Assert.True(managed.GetProperty("configured").GetBoolean(), "the managed value is configured");
            Assert.Equal("file", managed.GetProperty("source").GetString());
            Assert.True(managed.GetProperty("writable").GetBoolean(), "the managed file is writable");

            var ghost = entries.GetProperty("GHOST");
            Assert.False(ghost.GetProperty("configured").GetBoolean(), "an absent reference is unconfigured");
            Assert.False(ghost.TryGetProperty("source", out _), "an unconfigured reference has no source");
            Assert.True(ghost.GetProperty("writable").GetBoolean(), "a write could supply it");

            var fileText = File.ReadAllText(credentials.ManagedPath);
            Assert.True(fileText.Contains("MANAGED=", StringComparison.Ordinal), "the write persisted to the managed document");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Describe_RejectsOver64Refs()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot(root, new Dictionary<string, string>());
        try
        {
            var refs = Enumerable.Range(0, 65).Select(index => $"R{index}").ToArray();
            var args = JsonSerializer.SerializeToElement(new { refs });
            var response = registry.InvokeAsync(new RpcRequest("credentials/describe", args)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a 65-ref batch is refused");
            Assert.Equal("gateway/bad-request", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Describe_RejectsBadGrammar()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot(root, new Dictionary<string, string>());
        try
        {
            var args = JsonSerializer.SerializeToElement(new { refs = new[] { "bad-ref" } });
            var response = registry.InvokeAsync(new RpcRequest("credentials/describe", args)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a hyphenated name is outside the grammar");
            Assert.Equal("gateway/bad-request", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Set_StoresTheValue()
    {
        var root = TempRoot();
        var (ctx, registry, credentials) = Boot(root, new Dictionary<string, string>());
        try
        {
            var args = JsonSerializer.SerializeToElement(new { @ref = "TOKEN", value = "sk-set" });
            var response = registry.InvokeAsync(new RpcRequest("credentials/set", args)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the set must commit");
            var describe = registry.InvokeAsync(new RpcRequest("credentials/describe",
                JsonSerializer.SerializeToElement(new { refs = new[] { "TOKEN" } }))).GetAwaiter().GetResult();
            var entry = describe.Result!.Value.GetProperty("TOKEN");
            Assert.True(entry.GetProperty("configured").GetBoolean(), "the set value is configured");
            Assert.Equal("file", entry.GetProperty("source").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Set_RejectsEmptyValue()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot(root, new Dictionary<string, string>());
        try
        {
            var args = JsonSerializer.SerializeToElement(new { @ref = "TOKEN", value = "" });
            var response = registry.InvokeAsync(new RpcRequest("credentials/set", args)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "an empty value is refused");
            Assert.Equal("gateway/bad-request", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Set_ShadowedByEnvironment_SettlesRejected()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot(root, new Dictionary<string, string> { ["SHADOWED"] = "launch-time" });
        try
        {
            var args = JsonSerializer.SerializeToElement(new { @ref = "SHADOWED", value = "sk-set" });
            var response = registry.InvokeAsync(new RpcRequest("credentials/set", args)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a shadowed write is refused");
            Assert.Equal("credential/rejected", response.Error!.Code);
            Assert.Equal("SHADOWED", response.Error.Details!.Value.GetProperty("ref").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Unset_RemovesTheReference()
    {
        var root = TempRoot();
        var (ctx, registry, credentials) = Boot(root, new Dictionary<string, string>());
        try
        {
            credentials.SetAsync("TOKEN", "sk-set").GetAwaiter().GetResult();
            var args = JsonSerializer.SerializeToElement(new { @ref = "TOKEN" });
            var response = registry.InvokeAsync(new RpcRequest("credentials/unset", args)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the unset must commit");
            var describe = registry.InvokeAsync(new RpcRequest("credentials/describe",
                JsonSerializer.SerializeToElement(new { refs = new[] { "TOKEN" } }))).GetAwaiter().GetResult();
            Assert.False(describe.Result!.Value.GetProperty("TOKEN").GetProperty("configured").GetBoolean(),
                "the reference is gone after unset");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    private static (Context Ctx, HshRpcRegistry Registry, LocalCredentialsProvider Credentials) Boot(
        string root, Dictionary<string, string> environment)
    {
        var ctx = new Context();
        var credentials = new LocalCredentialsProvider(
            ctx,
            new LocalCredentialsConfig
            {
                ManagedPath = Path.Combine(root, ".credentials.env"),
                ProjectEnvPath = null,
                UserEnvPath = null,
            },
            name => environment.TryGetValue(name, out var value) ? value : null);
        var registry = new HshRpcRegistry(ctx);
        _ = registry.Register(global::Harness.Web.Host.CredentialsRemotes.Describe(ctx));
        _ = registry.Register(global::Harness.Web.Host.CredentialsRemotes.Set(ctx));
        _ = registry.Register(global::Harness.Web.Host.CredentialsRemotes.Unset(ctx));
        return (ctx, registry, credentials);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hsh-host-creds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
