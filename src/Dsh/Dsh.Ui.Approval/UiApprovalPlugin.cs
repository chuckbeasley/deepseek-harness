using Harness.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Harness.Ui.Approval;

/// <summary>
/// The in-shell approval host (port of the TS ui-approval answerer): one component rendering
/// into the shell's <c>shell.overlay</c> slot that answers the interaction waterfalls in-process —
/// the approve/deny dialog for <c>approval/request</c>, a text dialog for
/// <c>user-questions/ask</c>, and the tools/pre-execute adapter that routes tool calls through
/// the approval seam while the shell is live. Everything dies with the circuit.
/// </summary>
public static class UiApprovalPlugin
{
    /// <summary>Register the approval host; the returned disposer withdraws it.</summary>
    public static IDisposable Apply(SlotRegistry slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Register("shell.overlay", 10, () => FragmentFor<ApprovalHost>());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };
}
