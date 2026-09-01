using System.Reflection;

namespace Harness.Web.App;

/// <summary>
/// The routed pages contributed by ui-* packages: the static Router scans the shell assembly
/// plus every registered contribution assembly. The shell cannot reference the RCLs (they
/// reference the shell), so each ui-* package registers its own assembly at Apply time — before
/// the first request, so the Routes component always sees the full set.
/// </summary>
public sealed class PageAssemblyRegistry
{
    private readonly List<Assembly> _assemblies = new();

    /// <summary>Register one contribution assembly (idempotent).</summary>
    public void Register(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (_assemblies)
        {
            if (!_assemblies.Contains(assembly)) _assemblies.Add(assembly);
        }
    }

    /// <summary>Every registered contribution assembly, in registration order.</summary>
    public IReadOnlyList<Assembly> List()
    {
        lock (_assemblies) return _assemblies.ToArray();
    }
}
