using Dsh.Web.App;
using Dsh.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Dsh.Ui.Settings;

/// <summary>
/// The settings surface (port of the TS ui-settings pages): the <c>/settings</c> page showing the
/// settings document path and the redacted namespace catalog from the settings seam, plus a
/// sidebar nav link. Editing stays on the remote and CLI surfaces (documented reduction); the
/// page proves the seam in the GUI.
/// </summary>
public static class UiSettingsPlugin
{
    /// <summary>Register the settings page assembly, the nav link, and the page route; the returned disposer withdraws the link.</summary>
    public static IDisposable Apply(SlotRegistry slots, PageAssemblyRegistry pageAssemblies)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(pageAssemblies);
        pageAssemblies.Register(typeof(SettingsPage).Assembly);
        return slots.Register("sidebar", 40, () => FragmentFor<SettingsNavLink>());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };
}
