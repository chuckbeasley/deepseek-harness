using Dsh.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Dsh.Ui.Workspace;

/// <summary>
/// The workspace-list contribution (port of the TS ui-workspace browsing region): every workspace
/// in display order, rendered into the shell's <c>sidebar</c> slot, live-updating from the
/// workspace registry events. Display-only for now: workspace selection and session filtering are
/// a later surface (documented reduction).
/// </summary>
public static class UiWorkspacePlugin
{
    /// <summary>Register the workspace-list contribution; the returned disposer withdraws it.</summary>
    public static IDisposable Apply(SlotRegistry slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Register("sidebar", 30, () => FragmentFor<WorkspaceList>());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };
}
