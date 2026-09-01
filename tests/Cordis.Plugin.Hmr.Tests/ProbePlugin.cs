using Harness.Cordis.Core;
using Harness.Cordis.Plugin.Loader;

namespace Harness.Cordis.Plugin.Hmr.Tests;

/// <summary>Test plugin that records the config value it last saw and registers a service.</summary>
[CordisPlugin("probe")]
public sealed class ProbePlugin : ILoaderPlugin, IUpdatablePlugin
{
    /// <summary>The last config value seen by any instance, as text.</summary>
    public static string? Seen { get; set; }

    /// <summary>Number of times any instance applied or updated.</summary>
    public static int Calls { get; private set; }

    /// <inheritdoc/>
    public ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config)
    {
        Seen = ReadValue(config);
        Calls++;
        ctx.Set("probe", this);
        return ValueTask.FromResult<IDisposable?>(new Disposer(ctx));
    }

    /// <inheritdoc/>
    public ValueTask UpdateAsync(object? config)
    {
        Seen = ReadValue(config);
        Calls++;
        return ValueTask.CompletedTask;
    }

    private static string? ReadValue(object? config) =>
        config is Dictionary<string, object?> map && map.TryGetValue("value", out var value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private sealed class Disposer(Context ctx) : IDisposable
    {
        public void Dispose() => ctx.Remove("probe");
    }
}
