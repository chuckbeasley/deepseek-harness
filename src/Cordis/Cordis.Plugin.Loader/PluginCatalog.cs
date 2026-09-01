using System.Reflection;
using Harness.Cordis.Core;

namespace Harness.Cordis.Plugin.Loader;

/// <summary>
/// Marks a class as a loadable Cordis plugin and gives it a catalog name. This is the C#
/// adaptation of the module-specifier side of the vendored loader: a row imports a plugin TYPE
/// from an assembly/type catalog via reflection instead of an ESM module.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CordisPluginAttribute : Attribute
{
    /// <summary>Create the attribute with the catalog <paramref name="name"/>.</summary>
    public CordisPluginAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The catalog name rows use to import this plugin.</summary>
    public string Name { get; }
}

/// <summary>
/// A Cordis plugin application (C# counterpart of a Cordis plugin function). Implementations
/// register services and effects on <paramref name="ctx"/> and return the disposer that undoes
/// them; the loader runs that disposer when the owning entry is removed. Effects registered
/// outside the returned disposer stay on the shared context until the context disposes (Cordis
/// scopes them to the entry fiber; the port scopes by the plugin disposer because Harness.Cordis.Core
/// keeps one fiber per context).
/// </summary>
public interface ILoaderPlugin
{
    /// <summary>Apply the plugin on <paramref name="ctx"/> and return the disposer, or null.</summary>
    ValueTask<IDisposable?> ApplyAsync(Context ctx, object? config);
}

/// <summary>
/// Plugin that receives its config changes while its entry stays mounted (port of a Cordis plugin
/// listening to <c>internal/update</c>). Group rows implement this to re-reconcile children.
/// </summary>
public interface IUpdatablePlugin
{
    /// <summary>Apply the new <paramref name="config"/> in place.</summary>
    ValueTask UpdateAsync(object? config);
}

/// <summary>
/// Name-to-plugin catalog the loader uses to import rows (C# adaptation of the Node module
/// resolution behind <c>EntryTree.import</c>; there is no ESM in the port).
/// </summary>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, Func<ILoaderPlugin>> _factories = new(StringComparer.Ordinal);

    /// <summary>Register a plugin factory under <paramref name="name"/>.</summary>
    public void Register(string name, Func<ILoaderPlugin> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[name] = factory;
    }

    /// <summary>Register a singleton plugin instance under <paramref name="name"/>.</summary>
    public void Register(string name, ILoaderPlugin instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Register(name, () => instance);
    }

    /// <summary>Register a plugin type under <paramref name="name"/>; each import news one instance.</summary>
    public void Register<T>(string name) where T : ILoaderPlugin, new()
    {
        Register(name, () => new T());
    }

    /// <summary>Register a plugin type resolved reflectively at import time.</summary>
    public void RegisterType(string name, Type type)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(ILoaderPlugin).IsAssignableFrom(type))
        {
            throw new ArgumentException($"type {type.FullName} does not implement {nameof(ILoaderPlugin)}", nameof(type));
        }
        _factories[name] = () => (ILoaderPlugin)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Scan an assembly for concrete classes carrying <see cref="CordisPluginAttribute"/> and
    /// implementing <see cref="ILoaderPlugin"/>, registering each under its attribute name.
    /// </summary>
    public void ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ILoaderPlugin).IsAssignableFrom(type)) continue;
            var attribute = type.GetCustomAttribute<CordisPluginAttribute>();
            if (attribute is null) continue;
            var captured = type;
            Register(attribute.Name, () => (ILoaderPlugin)Activator.CreateInstance(captured)!);
        }
    }

    /// <summary>Resolve a plugin instance by catalog name, or null when unknown.</summary>
    public ILoaderPlugin? Resolve(string name)
    {
        return _factories.TryGetValue(name, out var factory) ? factory() : null;
    }
}
