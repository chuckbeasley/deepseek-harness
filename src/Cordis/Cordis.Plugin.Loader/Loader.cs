using Cordis.Core;

namespace Cordis.Plugin.Loader;

/// <summary>Root loader configuration (port of the vendored <c>Loader.Config</c>).</summary>
public sealed class LoaderConfig
{
    /// <summary>Base URL used to resolve relative plugin specifiers; informational in the port.</summary>
    public string? BaseUrl { get; set; }
}

/// <summary>
/// Service that owns a loader entry tree and imports configured plugins (C# port of the vendored
/// <c>Loader</c>). The loader registers itself on its context under the <c>loader</c> key, exposes
/// the plugin catalog and builtin table rows import from, and reconciles the root group.
/// Subclasses of <see cref="EntryTree"/> provide persistence by overriding <see cref="Write"/>.
/// </summary>
public sealed class Loader : EntryTree, IDisposable, IAsyncDisposable
{
    /// <summary>Create the loader on <paramref name="ctx"/> and register it as the <c>loader</c> service.</summary>
    public Loader(Context ctx, LoaderConfig? config = null)
        : base(ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (config?.BaseUrl is not null) BaseUrl = config.BaseUrl;
        ctx.Set("loader", this);
    }

    /// <summary>Builtin plugin table rows import via <c>cordis:&lt;name&gt;</c> specifiers.</summary>
    public Dictionary<string, object?> Builtins { get; } = new(StringComparer.Ordinal);

    /// <summary>Catalog used to import non-builtin plugin rows.</summary>
    public PluginCatalog Catalog { get; } = new();

    /// <summary>Base URL used to resolve relative plugin specifiers; informational in the port.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Name of the service this loader is registered under.</summary>
    public string Name => "loader";

    /// <summary>The loader's root tree is in-memory; writes are no-ops.</summary>
    public override void Write()
    {
    }

    /// <summary>Hook for hosts that can restart the process on full-reload requests.</summary>
    public void Exit()
    {
    }

    /// <summary>Return the loader entry id that owns <paramref name="fiber"/>, if any.</summary>
    public string? Locate(EntryFiber? fiber)
    {
        return fiber?.Entry.Id;
    }

    internal override Loader LoaderService => this;

    internal void ShowLog(Entry entry, string type)
    {
        if (entry.Options.Group == true || !EnableLogs) return;
        Ctx.Logger.Logger("loader").Info($"{type} plugin {entry.Options.Name}");
    }

    /// <summary>
    /// Unload the tree: dispose every mounted fiber, in reverse mount order, and mark the tree
    /// disposed so in-flight reconciliations skip rollback.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        MarkDisposed();
        return new ValueTask(Root.StopAsync());
    }

    /// <summary>Synchronous form of <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
