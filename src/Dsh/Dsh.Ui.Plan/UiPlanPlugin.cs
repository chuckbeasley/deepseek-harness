using Harness.Web.App;
using Harness.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Harness.Ui.Plan;

/// <summary>
/// The plan surface (port of the TS ui-plan): the <c>/plan</c> page showing the selected
/// session's plan fold from the plan seam, plus a sidebar nav link. The page follows the shared
/// selection and the store, so a live plan update re-renders it.
/// </summary>
public static class UiPlanPlugin
{
    /// <summary>Register the plan page assembly, the nav link, and the page route; the returned disposer withdraws the link.</summary>
    public static IDisposable Apply(SlotRegistry slots, PageAssemblyRegistry pageAssemblies)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(pageAssemblies);
        pageAssemblies.Register(typeof(PlanPage).Assembly);
        return slots.Register("sidebar", 50, () => FragmentFor<PlanNavLink>());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };
}
