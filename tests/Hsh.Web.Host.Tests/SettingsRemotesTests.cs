using Harness.Cordis.Core;
using Harness.Cordis.Schemastery;
using Harness.Settings;
using Harness.Web.Host;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The settings remotes: the redacted catalog, the update/replace writes over a real file provider,
/// and the refusal classification (settings/conflict vs settings/rejected), all through the
/// registry so the wire codes ride the responses.
/// </summary>
public static class SettingsRemotesTests
{
    public static void Describe_ReturnsRedactedCatalog()
    {
        var root = TempRoot();
        var (ctx, registry, settings) = Boot(root);
        try
        {
            var scope = settings.Register<Dictionary<string, object?>>("llm-test", TestSchema());
            scope.UpdateAsync(new Dictionary<string, object?>
            {
                ["apiKey"] = "sk-live-secret",
                ["model"] = "deepseek-reasoner",
            }).GetAwaiter().GetResult();

            var response = registry.InvokeAsync(new RpcRequest("settings/describe", null)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "describe must succeed");
            var payload = response.Result!.Value;
            Assert.True(payload.GetProperty("writable").GetBoolean(), "the file provider is writable");
            Assert.True(payload.GetProperty("hasDocument").GetBoolean(), "the file provider owns a document");
            var namespaces = payload.GetProperty("namespaces");
            Assert.True(namespaces.GetArrayLength() == 1, "one registered namespace");
            var ns = namespaces[0];
            Assert.Equal("llm-test", ns.GetProperty("ns").GetString());
            Assert.Equal("live", ns.GetProperty("applies").GetString());
            var value = ns.GetProperty("value");
            Assert.False(value.TryGetProperty("apiKey", out _), "the secret is stripped from the redacted value");
            Assert.Equal("deepseek-reasoner", value.GetProperty("model").GetString());
            Assert.True(ns.TryGetProperty("user", out _), "the raw user layer rides as user");
            Assert.False(ns.TryGetProperty("base", out _), "an absent base layer is omitted");
            var secrets = ns.GetProperty("secrets");
            Assert.True(secrets.GetArrayLength() == 1, "one schema-declared secret slot");
            Assert.Equal("apiKey", secrets[0].GetProperty("path")[0].GetString());
            Assert.True(secrets[0].GetProperty("set").GetBoolean(), "the slot currently holds a value");
            Assert.True(ns.GetProperty("revision").GetInt64() == 1, "the revision counts the write");
            var schema = ns.GetProperty("schema");
            Assert.True(schema.TryGetProperty("uid", out _) && schema.TryGetProperty("refs", out _),
                "the schema rides the toJSON refs envelope");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Update_MergesAndAnswersTheNewView()
    {
        var root = TempRoot();
        var (ctx, registry, settings) = Boot(root);
        try
        {
            _ = settings.Register<Dictionary<string, object?>>("llm-test", TestSchema());
            var args = JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                patch = new { model = "deepseek-reasoner" },
            });
            var response = registry.InvokeAsync(new RpcRequest("settings/update", args)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the update must commit");
            var ns = response.Result!.Value;
            Assert.Equal("deepseek-reasoner", ns.GetProperty("value").GetProperty("model").GetString());
            Assert.True(ns.GetProperty("revision").GetInt64() == 1, "the merge bumped the revision");

            var second = registry.InvokeAsync(new RpcRequest("settings/update", JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                patch = new { apiKey = "sk-rotated" },
            }))).GetAwaiter().GetResult();
            var secondNs = second.Result!.Value;
            Assert.Equal("deepseek-reasoner", secondNs.GetProperty("value").GetProperty("model").GetString(),
                "a patch merges, it does not replace");
            Assert.True(secondNs.GetProperty("revision").GetInt64() == 2, "each commit bumps the revision");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Update_StaleRevision_SettlesConflict()
    {
        var root = TempRoot();
        var (ctx, registry, settings) = Boot(root);
        try
        {
            _ = settings.Register<Dictionary<string, object?>>("llm-test", TestSchema());
            _ = registry.InvokeAsync(new RpcRequest("settings/update", JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                patch = new { model = "a" },
            }))).GetAwaiter().GetResult();

            var response = registry.InvokeAsync(new RpcRequest("settings/update", JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                patch = new { model = "b" },
                expectedRevision = 0,
            }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "a stale writer is refused");
            Assert.Equal("settings/conflict", response.Error!.Code);
            var details = response.Error.Details!.Value;
            Assert.Equal("llm-test", details.GetProperty("ns").GetString());
            Assert.True(details.GetProperty("expected").GetInt64() == 0, "the refused revision rides along");
            Assert.True(details.GetProperty("actual").GetInt64() == 1, "the standing revision rides along");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Update_UnknownNamespace_SettlesRejected()
    {
        var root = TempRoot();
        var (ctx, registry, settings) = Boot(root);
        try
        {
            var response = registry.InvokeAsync(new RpcRequest("settings/update", JsonSerializer.SerializeToElement(new
            {
                ns = "ghost",
                patch = new { model = "a" },
            }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "an unknown namespace write is refused");
            Assert.Equal("settings/rejected", response.Error!.Code);
            Assert.Equal("ghost", response.Error.Details!.Value.GetProperty("ns").GetString());
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Replace_ReplacesWholesale()
    {
        var root = TempRoot();
        var (ctx, registry, settings) = Boot(root);
        try
        {
            _ = settings.Register<Dictionary<string, object?>>("llm-test", TestSchema());
            _ = registry.InvokeAsync(new RpcRequest("settings/update", JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                patch = new { model = "deepseek-reasoner" },
            }))).GetAwaiter().GetResult();

            var response = registry.InvokeAsync(new RpcRequest("settings/replace", JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                section = new Dictionary<string, object?>(),
            }))).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the replace must commit");
            var value = response.Result!.Value.GetProperty("value");
            Assert.Equal("deepseek-chat", value.GetProperty("model").GetString(),
                "an empty section re-inherits the schema default");
            Assert.True(response.Result.Value.GetProperty("revision").GetInt64() == 2, "the replace bumped the revision");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Describe_WithoutProvider_SettlesInternal()
    {
        var ctx = new Context();
        try
        {
            var registry = new HshRpcRegistry(ctx);
            _ = registry.Register(global::Harness.Web.Host.SettingsRemotes.Describe(ctx));
            var response = registry.InvokeAsync(new RpcRequest("settings/describe", null)).GetAwaiter().GetResult();
            Assert.False(response.Ok, "no provider means no catalog");
            Assert.Equal("gateway/internal", response.Error!.Code);
            Assert.True(response.Error.Message.Contains("settings provider", StringComparison.Ordinal),
                "the diagnostic says how to supply the provider");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void Mutate_AppliesPathOpsAndAnswersTheNewView()
    {
        var root = TempRoot();
        var (ctx, registry, settings) = Boot(root);
        try
        {
            _ = settings.Register<Dictionary<string, object?>>("llm-test", TestSchema());
            var args = JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                ops = new object[]
                {
                    new { op = "set", path = new[] { "model" }, value = "deepseek-reasoner" },
                    new { op = "set", path = new[] { "apiKey" }, value = "sk-rotated" },
                },
            });
            var response = registry.InvokeAsync(new RpcRequest("settings/mutate", args)).GetAwaiter().GetResult();
            Assert.True(response.Ok, "the mutate must commit");
            var ns = response.Result!.Value;
            Assert.Equal("deepseek-reasoner", ns.GetProperty("value").GetProperty("model").GetString());
            Assert.False(ns.GetProperty("value").TryGetProperty("apiKey", out _), "the view stays redacted");
            Assert.True(ns.GetProperty("secrets").GetArrayLength() == 1, "the secret slot is reported");
            Assert.True(ns.GetProperty("secrets")[0].GetProperty("set").GetBoolean(), "the path op set the secret");
            Assert.True(ns.GetProperty("revision").GetInt64() == 1, "the mutate bumped the revision");

            var unset = registry.InvokeAsync(new RpcRequest("settings/mutate", JsonSerializer.SerializeToElement(new
            {
                ns = "llm-test",
                ops = new object[] { new { op = "unset", path = new[] { "model" } } },
            }))).GetAwaiter().GetResult();
            Assert.True(unset.Ok, "the unset must commit");
            Assert.Equal("deepseek-chat", unset.Result!.Value.GetProperty("value").GetProperty("model").GetString(),
                "an unset re-inherits the schema default");
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Mutate_BadOpShape_SettlesBadRequest()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot(root);
        try
        {
            foreach (var ops in new object[]
            {
                new object[] { new { op = "bogus", path = new[] { "model" } } },
                new object[] { new { op = "set", path = new[] { 1 } } },
                new object[] { new { op = "unset" } },
            })
            {
                var response = registry.InvokeAsync(new RpcRequest("settings/mutate",
                    JsonSerializer.SerializeToElement(new { ns = "llm-test", ops }))).GetAwaiter().GetResult();
                Assert.False(response.Ok, "a malformed op is refused before the write");
                Assert.Equal("gateway/bad-request", response.Error!.Code);
            }
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    public static void Mutate_UnknownNamespace_SettlesRejected()
    {
        var root = TempRoot();
        var (ctx, registry, _) = Boot(root);
        try
        {
            var response = registry.InvokeAsync(new RpcRequest("settings/mutate", JsonSerializer.SerializeToElement(new
            {
                ns = "ghost",
                ops = new object[] { new { op = "set", path = new[] { "model" }, value = "a" } },
            }))).GetAwaiter().GetResult();
            Assert.False(response.Ok, "an unknown namespace write is refused");
            Assert.Equal("settings/rejected", response.Error!.Code);
        }
        finally
        {
            ctx.Dispose();
            Cleanup(root);
        }
    }

    private static (Context Ctx, HshRpcRegistry Registry, FileSettingsProvider Settings) Boot(string root)
    {
        var ctx = new Context();
        var settings = new FileSettingsProvider(ctx, Path.Combine(root, "settings.json"));
        settings.StartAsync().GetAwaiter().GetResult();
        var registry = new HshRpcRegistry(ctx);
        _ = registry.Register(global::Harness.Web.Host.SettingsRemotes.Describe(ctx));
        _ = registry.Register(global::Harness.Web.Host.SettingsRemotes.Update(ctx));
        _ = registry.Register(global::Harness.Web.Host.SettingsRemotes.Replace(ctx));
        _ = registry.Register(global::Harness.Web.Host.SettingsRemotes.Mutate(ctx));
        return (ctx, registry, settings);
    }

    private static Schema TestSchema()
        => Schema.Object(new Dictionary<string, Schema>
        {
            ["apiKey"] = Schema.String().Role("secret"),
            ["model"] = Schema.String().Default("deepseek-chat"),
        });

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hsh-host-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
