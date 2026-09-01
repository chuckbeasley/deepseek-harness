using Harness.Cordis.Core;
using Harness.Cordis.Plugin.Include;
using Harness.Cordis.Plugin.Loader;
using IncludePlugin = global::Harness.Cordis.Plugin.Include.Include;
using IncludePluginConfig = global::Harness.Cordis.Plugin.Include.IncludeConfig;

namespace Harness.Cordis.Plugin.Hmr.Tests;

/// <summary>
/// Behavior tests for the Phase 1 HMR port. Each test boots a fresh context with the loader and a
/// registered probe plugin, applies an include, registers the HMR watch, and poll-waits up to five
/// seconds for watcher-driven refreshes.
/// </summary>
internal static class HmrTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    private static (Context Ctx, global::Harness.Cordis.Plugin.Loader.Loader Loader) Boot()
    {
        var ctx = new Context();
        var loader = new global::Harness.Cordis.Plugin.Loader.Loader(ctx);
        loader.Catalog.RegisterType("probe", typeof(ProbePlugin));
        return (ctx, loader);
    }

    private static string TempConfig(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), $"hsh-hmr-{Guid.NewGuid():N}.yml");
        File.WriteAllText(file, content);
        return file;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"timed out after {PollTimeout} waiting for {what}");
            }
            await Task.Delay(50);
        }
    }

    public static async Task RegisterWatchesFileAndRefreshes()
    {
        ProbePlugin.Seen = null;
        var file = TempConfig("- id: svc\n  name: probe\n  config:\n    value: one\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new IncludePlugin(ctx, new IncludePluginConfig { Path = file });
            await include.ApplyFileAsync();
            Assert.Equal("one", ProbePlugin.Seen);
            var hmr = new Hmr(ctx, new HmrConfig()); // default debounce
            var disposer = await hmr.RegisterConfigAsync(include);

            File.WriteAllText(file, "- id: svc\n  name: probe\n  config:\n    value: two\n");
            await WaitForAsync(() => ProbePlugin.Seen == "two", "the include to refresh after the config change");
            Assert.True(ctx.Get<ProbePlugin>("probe") is not null, "the probe service must stay mounted");
            await disposer.DisposeAsync();
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task MissingParentBecomesWatchable()
    {
        ProbePlugin.Seen = null;
        // Pre-create a test-owned root so the watcher watches only this subtree, then register a
        // path under two missing parent directories.
        var root = Path.Combine(Path.GetTempPath(), $"hsh-hmr-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a", "b", "config.yml");
        try
        {
            var (ctx, _) = Boot();
            var include = new IncludePlugin(ctx, new IncludePluginConfig { Path = file });
            var hmr = new Hmr(ctx, new HmrConfig { DebounceMs = 50 });
            var disposer = await hmr.RegisterConfigAsync(include); // must not throw while parents are missing

            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            File.WriteAllText(file, "- id: svc\n  name: probe\n  config:\n    value: appears\n");
            await WaitForAsync(() => ProbePlugin.Seen == "appears", "the include to refresh once the missing parents exist");
            Assert.True(ctx.Get<ProbePlugin>("probe") is not null, "the probe service must be mounted");
            await disposer.DisposeAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // The watcher may briefly hold the tree; the temp root is cleaned by the OS eventually.
            }
        }
    }

    public static async Task BrokenRefreshLogsAndContinues()
    {
        ProbePlugin.Seen = null;
        var file = TempConfig("- id: svc\n  name: probe\n  config:\n    value: one\n");
        try
        {
            var (ctx, _) = Boot();
            var failures = new List<(string Filename, Exception Error)>();
            ctx.On("hmr/config-update-failed", (Action<string, Exception>)((filename, error) =>
            {
                failures.Add((filename, error));
                throw new InvalidOperationException("observer failure must be contained");
            }));
            var include = new IncludePlugin(ctx, new IncludePluginConfig { Path = file });
            await include.ApplyFileAsync();
            var hmr = new Hmr(ctx, new HmrConfig { DebounceMs = 50 });
            var disposer = await hmr.RegisterConfigAsync(include);

            // A top-level map is not an entry list: the refresh fails, is logged, and is broadcast.
            File.WriteAllText(file, "not: [valid: yaml\n");
            await WaitForAsync(() => failures.Count > 0, "the failed refresh to be broadcast");
            Assert.True(ctx.Logger.Buffer.Any(message =>
                    message.Type == "warn" && message.Name == "hmr"
                    && message.Args.OfType<string>().Any(arg => arg.Contains("config reload at", StringComparison.Ordinal))),
                "the failed refresh must be logged as a warning");

            // The watcher survives the failure: a valid config refreshes successfully afterwards.
            File.WriteAllText(file, "- id: svc\n  name: probe\n  config:\n    value: two\n");
            await WaitForAsync(() => ProbePlugin.Seen == "two", "the include to refresh after the broken config");
            Assert.Equal(1, failures.Count, "exactly one refresh failure must be broadcast");
            await disposer.DisposeAsync();
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task DisposingStopsFurtherRefreshes()
    {
        ProbePlugin.Seen = null;
        var file = TempConfig("- id: svc\n  name: probe\n  config:\n    value: one\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new IncludePlugin(ctx, new IncludePluginConfig { Path = file });
            await include.ApplyFileAsync();
            var hmr = new Hmr(ctx, new HmrConfig { DebounceMs = 50 });
            var disposer = await hmr.RegisterConfigAsync(include);

            File.WriteAllText(file, "- id: svc\n  name: probe\n  config:\n    value: two\n");
            await WaitForAsync(() => ProbePlugin.Seen == "two", "the first refresh to complete");
            var callsAfterFirstRefresh = ProbePlugin.Calls;

            await disposer.DisposeAsync();
            File.WriteAllText(file, "- id: svc\n  name: probe\n  config:\n    value: three\n");
            // Wait well past the debounce window plus watcher latency before asserting.
            await Task.Delay(1500);

            Assert.Equal("two", ProbePlugin.Seen, "a disposed registration must not refresh");
            Assert.Equal(callsAfterFirstRefresh, ProbePlugin.Calls, "a disposed registration must not apply again");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void DuplicateRegistrationThrows()
    {
        var file = TempConfig("- name: probe\n");
        try
        {
            var (ctx, _) = Boot();
            var hmr = new Hmr(ctx, new HmrConfig());
            var disposer = hmr.RegisterConfigAsync(file, () => Task.CompletedTask).GetAwaiter().GetResult();
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => _ = hmr.RegisterConfigAsync(file, () => Task.CompletedTask),
                    "registering the same config path twice must fail loud");
            }
            finally
            {
                disposer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
